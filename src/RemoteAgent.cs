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
        sealed class Packet
        {
            static long _serial;
            static readonly string Session=Guid.NewGuid().ToString("N");
            public readonly string Json,Id;public readonly long Tick;public readonly int MaxAge;
            public Packet(Snapshot sample):this(sample,10){}
            public Packet(Snapshot sample,int interval){Json=RemoteAgent.Json(sample);Tick=Stopwatch.GetTimestamp();MaxAge=Math.Max(15000,interval*2000+5000);Id=Session+"-"+Interlocked.Increment(ref _serial);}
        }
        sealed class Holder {public volatile ProbeConfig Settings;public volatile Packet Latest;}
        static string Response(Packet packet,int idle)
        {
            if(packet==null)return "{\"error\":\"no sample yet\"}";
            double age=(Stopwatch.GetTimestamp()-packet.Tick)*1000.0/Stopwatch.Frequency;
            double limit=packet.MaxAge;
            if(age>limit)return "{\"error\":\"stale sample\"}";
            return "{\"sampleId\":\""+packet.Id+"\",\"sampleAgeMs\":"+N(age)+",\"maxAgeMs\":"+N(limit)+","+packet.Json.Substring(1);
        }
        static int Serve(string[] args)
        {
            if(args[0]!="--serve-config"||args.Length!=2){Console.Error.WriteLine("Use --serve-config <path>; command-line secrets are no longer supported.");return 1;}
            string path=args[1];var h=new Holder{Settings=ProbeConfig.Load(path)};
            var sampler=new LocalSampler();sampler.Prime();
            var worker=new Thread(delegate(){while(true){
                var clock=Stopwatch.StartNew();Snapshot sample;
                try{sample=sampler.Sample(1000);}catch{sample=new Snapshot{Ok=false,Error="sampling failed"};}
                var config=h.Settings;
                int interval=sample.Ok&&sample.CpuPct<8&&sample.GpuPct<8&&sample.DiskActive<10?config.Idle:config.Interval;
                h.Latest=new Packet(sample,interval);
                Thread.Sleep(Math.Max(200,interval*1000-(int)clock.ElapsedMilliseconds));
            }});worker.IsBackground=true;worker.Start();
            ProbeConfig active=h.Settings;var listener=new TcpListener(IPAddress.Parse(active.Bind),active.Port);listener.Start();
            var slots=new SemaphoreSlim(8,8);DateTime stamp=File.GetLastWriteTimeUtc(path);
            Log("probe protocol v3 started; TLS="+(active.CertificateThumbprint.Length>0));
            while(true){
                if(File.GetLastWriteTimeUtc(path)!=stamp){
                    try{
                        var next=ProbeConfig.Load(path);
                        if(next.CertificateThumbprint.Length>0)using(var cert=ProbeWire.ServerCertificate(next.CertificateThumbprint)){}
                        if(next.Port!=active.Port||next.Bind!=active.Bind){
                            var replacement=new TcpListener(IPAddress.Parse(next.Bind),next.Port);
                            if(next.Port==active.Port){listener.Stop();try{replacement.Start();}catch{listener.Start();throw;}}
                            else replacement.Start();listener.Stop();listener=replacement;
                        }
                        active=next;h.Settings=next;Log("configuration applied");
                    }catch{Log("configuration update rejected");}
                    stamp=File.GetLastWriteTimeUtc(path);
                }
                if(!listener.Pending()){Thread.Sleep(25);continue;}
                TcpClient client=listener.AcceptTcpClient();
                ProbeConfig captured=active;
                if(!captured.Allows(((IPEndPoint)client.Client.RemoteEndPoint).Address)||!slots.Wait(0)){client.Close();continue;}
                ThreadPool.QueueUserWorkItem(delegate(object ignored){
                    try{
                        // Absolute deadline includes TLS handshake, authentication and send, not just individual reads.
                        using(client)using(var deadline=new Timer(delegate{try{client.Close();}catch{}},null,3000,Timeout.Infinite))
                        using(var cert=captured.CertificateThumbprint.Length>0?ProbeWire.ServerCertificate(captured.CertificateThumbprint):null)
                        using(Stream stream=ProbeWire.Server(client.GetStream(),cert)){
                            stream.ReadTimeout=3000;stream.WriteTimeout=3000;
                            string line=ProbeWire.ReadLine(stream,128,3000);
                            string payload=ConstantEquals(line,captured.Token)?Response(h.Latest,h.Settings.Idle):"{\"error\":\"bad token\"}";
                            byte[] bytes=Encoding.UTF8.GetBytes(payload+"\n");stream.Write(bytes,0,bytes.Length);stream.Flush();
                        }
                    }catch{Log("client request rejected or timed out");}finally{slots.Release();}
                });
            }
        }
        static bool ConstantEquals(string a,string b)
        {
            int diff=a.Length^b.Length;int n=Math.Max(a.Length,b.Length);
            for(int i=0;i<n;i++)diff|=(i<a.Length?a[i]:0)^(i<b.Length?b[i]:0);
            return diff==0;
        }

        static string SafeRemote(TcpClient c)
        {
            try { return ((IPEndPoint)c.Client.RemoteEndPoint).Address.ToString(); }
            catch { return "?"; }
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
            b.Append("\"protocolVersion\":3,\"ok\":").Append(s.Ok?"true":"false").Append(',');
            if(!s.Ok)Str2(b,"error","sampling failed",true);
            Str2(b,"sampledAtUtc",s.At.ToUniversalTime().ToString("o"),true);
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
