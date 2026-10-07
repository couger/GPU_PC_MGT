// RemoteAgent.cs -- the probe that runs ON the monitored machine.
//
// Two modes:
//   gpm-remote.exe [0|1]                     one-shot: sample once, print one JSON line (SSH fallback)
//   gpm-remote.exe --serve <port> <token> [intervalSec] [idleIntervalSec] [bind]
//                                            resident: sample on a timer, serve the latest JSON
//                                            line over TCP (default channel, no SSH involved)
//
// Build: csc /platform:x64 /target:exe /out:gpm-remote.exe src\RemoteAgent.cs src\Model.cs src\Native.cs src\LocalSampler.cs
// NOTE: keep this file ASCII-only (csc may read a BOM-less file with the ANSI code page).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Gpm
{
    static class RemoteAgent
    {
        static int Main(string[] args)
        {
            if(args.Length>0&&args[0]=="--init-config")
            {
                try{ProbeConfig.Initialize(args.Length>1?args[1]:ProbeConfig.DefaultPath);Console.WriteLine("Probe configuration created. Copy connection credentials from the probe tray settings.");return 0;}
                catch(Exception){Console.Error.WriteLine("Cannot create probe configuration; existing files are preserved. Check path and permissions.");return 1;}
            }
            if (args.Length > 0 && (args[0] == "--serve" || args[0] == "--serve-config")) return Serve(args);
            return Once();
        }

        // ============================================================ one-shot mode
        static int Once()
        {
            try
            {
                Snapshot s;
                using (LocalSampler ls = new LocalSampler())
                {
                    ls.Prime();
                    Thread.Sleep(1050);          // PDH rate counters need two samples ~1s apart
                    s = ls.Sample(1000);
                }
                Console.Out.WriteLine(Json(s));
                return 0;
            }
            catch (Exception ex)
            {
                Console.Out.WriteLine("{\"error\":\"" + Esc(ex.Message) + "\"}");
                return 1;
            }
        }

        // ============================================================ resident mode
        class Holder
        {
            public volatile ProbeConfig Settings;
            public volatile string Json = "";
            public DateTime At = DateTime.MinValue;
            public bool Ok;
            public string LastError = "";
        }

        static int Serve(string[] args)
        {
            string configPath=args[0]=="--serve-config"?args[1]:null;
            ProbeConfig settings=configPath!=null?ProbeConfig.Load(configPath):null;
            int port = args.Length > 1 ? ToInt(args[1], 9187) : 9187;
            string token = args.Length > 2 ? args[2] : "";
            int interval = args.Length > 3 ? ToInt(args[3], 2) : 2;
            int idle = args.Length > 4 ? ToInt(args[4], 10) : 10;
            string bind = args.Length > 5 ? args[5] : "0.0.0.0";
            if(settings!=null){port=settings.Port;token=settings.Token;interval=settings.Interval;idle=settings.Idle;bind=settings.Bind;}
            if(token.Length==0)throw new ArgumentException("A probe token is required.");
            if (interval < 1) interval = 1;
            if (idle < interval) idle = interval;

            Log("serve mode starting: port=" + port + " bind=" + bind + " interval=" + interval + "s idle=" + idle + "s token=" + (token.Length > 0 ? "set" : "EMPTY"));

            Holder h = new Holder();
            h.Settings=settings;

            // ---- sampling thread: keeps the latest JSON fresh, sleeping when the machine is idle
            LocalSampler sampler = new LocalSampler();
            sampler.Prime();
            Thread worker = new Thread(delegate()
            {
                while (true)
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    try
                    {
                        Snapshot s = sampler.Sample(1000);
                        h.Json = Json(s);
                        h.At = DateTime.Now;
                        h.Ok = s.Ok;
                        h.LastError = s.Error;
                        if (!s.Ok && h.LastError.Length > 0) Log("sample error: " + h.LastError);
                    }
                    catch (Exception ex)
                    {
                        h.LastError = ex.Message;
                        Log("sample exception: " + ex.Message);
                    }
                    sw.Stop();
                    bool busy = h.Ok && (true);
                    // adaptive: keep the fast cadence only while the machine is actually doing something
                    ProbeConfig current=h.Settings;
                    int iv = current!=null?current.Interval:interval;
                    if (busy)
                    {
                        // parse the fields we need from the JSON text (cheap, no re-parse of the object)
                        double cpu = FieldOf(h.Json, "\"cpuPct\":");
                        double gpu = FieldOf(h.Json, "\"gpuPct\":");
                        double disk = FieldOf(h.Json, "\"diskActive\":");
                        if (cpu < 8 && gpu < 8 && disk < 10) iv = current!=null?current.Idle:idle;
                    }
                    int wait = Math.Max(200, iv * 1000 - (int)sw.ElapsedMilliseconds);
                    Thread.Sleep(wait);
                }
            });
            worker.IsBackground = true;
            worker.Name = "sampler";
            worker.Start();

            // ---- serving loop: one line per connection, token first
            IPAddress addr = IPAddress.Any;
            try { addr = IPAddress.Parse(bind); }
            catch { Log("bad bind '" + bind + "', using 0.0.0.0"); }
            TcpListener listener = new TcpListener(addr, port);
            listener.Start();
            Log("listening on " + addr + ":" + port);
            DateTime configStamp=configPath!=null?File.GetLastWriteTimeUtc(configPath):DateTime.MinValue;

            while (true)
            {
                TcpClient client = null;
                try
                {
                    if(configPath!=null&&File.GetLastWriteTimeUtc(configPath)!=configStamp)
                    {
                        try
                        {
                            ProbeConfig next=ProbeConfig.Load(configPath);
                            if(next.Port!=port||next.Bind!=bind)
                            {
                                // Bind the replacement before releasing the working listener where possible.
                                TcpListener replacement=new TcpListener(IPAddress.Parse(next.Bind),next.Port);
                                if(next.Port==port){listener.Stop();try{replacement.Start();}catch{listener.Start();throw;}}
                                else replacement.Start();
                                listener.Stop();listener=replacement;port=next.Port;bind=next.Bind;
                            }
                            settings=next;h.Settings=next;token=next.Token;configStamp=File.GetLastWriteTimeUtc(configPath);Log("configuration applied");
                        }
                        catch(Exception){Log("configuration update rejected; retaining previous settings");configStamp=File.GetLastWriteTimeUtc(configPath);}
                    }
                    if(!listener.Pending()){Thread.Sleep(200);continue;}
                    client = listener.AcceptTcpClient();
                    if(settings!=null&&!settings.Allows(((IPEndPoint)client.Client.RemoteEndPoint).Address)){Log("rejected client "+SafeRemote(client)+" (address not allowed)");continue;}
                    client.ReceiveTimeout = 3000;
                    client.SendTimeout = 3000;
                    NetworkStream ns = client.GetStream();

                    string line = ReadLine(ns, 256);
                    string payload;
                    if (token.Length > 0 && line != token)
                    {
                        payload = "{\"error\":\"bad token\"}";
                        Log("rejected client " + SafeRemote(client) + " (bad token)");
                    }
                    else if (h.Json.Length == 0)
                    {
                        payload = "{\"error\":\"no sample yet\"}";
                    }
                    else
                    {
                        payload = h.Json;
                    }
                    byte[] outb = Encoding.UTF8.GetBytes(payload + "\n");
                    ns.Write(outb, 0, outb.Length);
                    ns.Flush();
                }
                catch (Exception ex)
                {
                    Log("connection error: " + ex.Message);
                }
                finally
                {
                    if (client != null) { try { client.Close(); } catch { } }
                }
            }
        }

        static string SafeRemote(TcpClient c)
        {
            try { return ((IPEndPoint)c.Client.RemoteEndPoint).Address.ToString(); }
            catch { return "?"; }
        }

        /// <summary>read one LF-terminated line (ASCII) with a hard cap</summary>
        static string ReadLine(NetworkStream ns, int max)
        {
            StringBuilder sb = new StringBuilder();
            byte[] buf = new byte[64];
            while (sb.Length <= max)
            {
                int n = ns.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                for (int i = 0; i < n; i++)
                {
                    if (buf[i] == (byte)'\n') return sb.ToString().Trim();
                    sb.Append((char)buf[i]);
                }
            }
            return sb.ToString().Trim();
        }

        /// <summary>grab a numeric field out of the JSON text without building an object</summary>
        static double FieldOf(string json, string key)
        {
            try
            {
                int i = json.IndexOf(key, StringComparison.Ordinal);
                if (i < 0) return 0;
                i += key.Length;
                int j = i;
                while (j < json.Length && (char.IsDigit(json[j]) || json[j] == '.' || json[j] == '-' || json[j] == 'e' || json[j] == 'E' || json[j] == '+')) j++;
                double v;
                if (double.TryParse(json.Substring(i, j - i), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            }
            catch { }
            return 0;
        }

        static int ToInt(string s, int def)
        {
            int v;
            if (int.TryParse(s, out v)) return v;
            return def;
        }

        static void Log(string msg)
        {
            try
            {
                string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe.log");
                LocalFiles.Append(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + "\r\n",65536);
            }
            catch { }
        }

        // ============================================================ JSON output
        static string N(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            return v.ToString("0.####", CultureInfo.InvariantCulture);
        }

        static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder b = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') b.Append('\\').Append(c);
                else if (c == '\n') b.Append("\\n");
                else if (c == '\r') b.Append("\\r");
                else if (c < ' ' || c > 126) b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else b.Append(c);
            }
            return b.ToString();
        }

        static void Num(StringBuilder b, string k, double v, bool comma)
        {
            b.Append('"').Append(k).Append("\":").Append(N(v));
            if (comma) b.Append(',');
        }

        static void Str2(StringBuilder b, string k, string v, bool comma)
        {
            b.Append('"').Append(k).Append("\":\"").Append(Esc(v)).Append('"');
            if (comma) b.Append(',');
        }

        static string Json(Snapshot s)
        {
            StringBuilder b = new StringBuilder(2048);
            b.Append('{');
            Str2(b, "host", s.Host, true);
            Str2(b, "os", s.Os, true);
            Str2(b, "cpu", s.CpuName, true);
            Str2(b, "gpu", s.GpuName, true);
            Str2(b, "uptime", s.Uptime, true);
            Num(b, "ramTotal", s.RamTotalBytes, true);
            Num(b, "cpuPct", s.CpuPct, true);
            Num(b, "cpuMhz", s.CpuMhz, true);

            b.Append("\"cores\":[");
            for (int i = 0; i < s.Cores.Count; i++)
            {
                if (i > 0) b.Append(',');
                b.Append(N(s.Cores[i]));
            }
            b.Append("],");

            Num(b, "memTotal", s.MemTotalBytes, true);
            Num(b, "memAvail", s.MemAvailBytes, true);
            Num(b, "memCommitted", s.MemCommittedBytes, true);
            Num(b, "diskActive", s.DiskActive, true);
            Num(b, "diskRead", s.DiskReadBps, true);
            Num(b, "diskWrite", s.DiskWriteBps, true);
            Num(b, "diskQueue", s.DiskQueue, true);
            Num(b, "netRx", s.NetRxBps, true);
            Num(b, "netTx", s.NetTxBps, true);
            Str2(b, "netAdapter", s.NetAdapter, true);
            Num(b, "gpuPct", s.GpuPct, true);

            b.Append("\"engines\":{");
            bool first = true;
            foreach (KeyValuePair<string, double> kv in s.Engines)
            {
                if (!first) b.Append(',');
                first = false;
                b.Append('"').Append(Esc(kv.Key)).Append("\":").Append(N(kv.Value));
            }
            b.Append("},");

            Num(b, "vramUsed", s.VramUsed, true);
            Num(b, "vramShared", s.VramShared, true);
            Num(b, "gpuTemp", s.GpuTempC, true);
            Num(b, "gpuFan", s.GpuFanRpm, true);
            Num(b, "gpuPower", s.GpuPowerPct, true);
            Num(b, "sysTemp", s.SysTempC, true);

            b.Append("\"volumes\":[");
            for (int i = 0; i < s.Volumes.Count; i++)
            {
                Volume v = s.Volumes[i];
                if (i > 0) b.Append(',');
                b.Append("{\"d\":\"").Append(Esc(v.Drive)).Append("\",\"size\":").Append(N(v.SizeBytes))
                 .Append(",\"free\":").Append(N(v.FreeBytes)).Append('}');
            }
            b.Append("],");

            Str2(b, "diag", "native-agent v2 cores=" + s.Cores.Count, false);
            b.Append('}');
            return b.ToString();
        }
    }
}
