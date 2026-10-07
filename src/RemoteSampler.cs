using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Gpm
{
    /// <summary>
    /// 远程采集：
    ///   1) 首次把内嵌的 PowerShell 载荷 base64 部署到远端 C:\ProgramData\GPUPCMGT\sampler.ps1
    ///   2) 之后每轮只通过 ssh 执行一条小命令（不再传 14KB 脚本、不用 stdin，快且稳）
    /// </summary>
    public class RemoteSampler
    {
        readonly string RemoteDir;
        readonly string RemoteScript;
        readonly string RemoteB64;
        readonly string RemoteAgent;
        readonly string RemoteAgentB64;

        readonly Target _t;
        readonly string _payload;
        readonly string _hash;
        readonly byte[] _agent;      // 原生采集器 gpm-remote.exe（首选：远端不起 PowerShell、不编译、不写临时文件）
        bool _agentDeployed;
        int _agentFails;
        bool _deployed;
        string _lastOut = "", _lastErr = "", _lastCmd = "";
        int _lastExit = -1;
        public string LastDiag = "";
        string _diagnosticError="";
        DateTime _lastDiagnosticUtc=DateTime.MinValue;
        bool? _connectionOk;

        public RemoteSampler(Target t)
        {
            _t = t;
            // 探针目录可配置（默认 C:\ProgramData\GPUPCMGT）；远端 cmd 会按空格拆参数，所以路径不能有空格
            string rdir = (t.RemoteDir == null ? "" : t.RemoteDir.Trim());
            if (rdir.Length == 0 || rdir.IndexOf(' ') >= 0) rdir = @"C:\ProgramData\GPUPCMGT";
            RemoteDir = rdir.TrimEnd('\\');
            RemoteScript = RemoteDir + @"\sampler.ps1";
            RemoteB64 = RemoteDir + @"\sampler.b64";
            RemoteAgent = RemoteDir + @"\gpm-remote.exe";
            RemoteAgentB64 = RemoteDir + @"\gpm-remote.b64";
            _payload = ReadResource("Gpm.remote-sampler.ps1");
            try
            {
                string ap = Path.Combine(Config.Dir, "gpm-remote.exe");
                if (File.Exists(ap)) _agent = File.ReadAllBytes(ap);
            }
            catch { }
            _hash = Hash(_payload);
        }

        static string Hash(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; }
                return h.ToString("x8");
            }
        }

        string StatePath { get { return Path.Combine(Config.DataDir, "ssh-state.txt"); } }
        string StateKey { get { return _t.User + "@" + _t.Host + ":" + _t.Port; } }

        /// <summary>部署缓存的键：模式前缀 + 内容哈希（换模式或换文件都会自动重传）</summary>
        string CurrentHash()
        {
            bool agent = (_agent != null && _agentFails < 2);
            return RemoteDir + "|" + (agent ? "A" : "P") + (agent ? Hash(Convert.ToBase64String(_agent)) : _hash);   // 换目录也要重传
        }

        /// <summary>本地记录"这个载荷已经部署过"，避免每次启动 exe 都重传一遍（省 15 秒以上）</summary>
        bool DeployedBefore()
        {
            try
            {
                if (!File.Exists(StatePath)) return false;
                string prefix = StateKey + "=";
                foreach (string line in File.ReadAllLines(StatePath))
                    if (line.StartsWith(prefix) && line.Substring(prefix.Length).Trim() == CurrentHash()) return true;
            }
            catch { }
            return false;
        }

        void SaveState(bool deployed)
        {
            try
            {
                List<string> lines = new List<string>();
                string prefix = StateKey + "=";
                if (File.Exists(StatePath))
                    foreach (string l in File.ReadAllLines(StatePath))
                        if (!l.StartsWith(prefix)) lines.Add(l);
                if (deployed) lines.Add(prefix + CurrentHash());
                File.WriteAllLines(StatePath, lines.ToArray(), new UTF8Encoding(false));
            }
            catch { }
        }

        static string ReadResource(string name)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream(name))
            {
                if (s == null) throw new Exception("缺少内嵌资源 " + name);
                using (StreamReader r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }

        // ---------------------------------------------------------------- 命令构造
        StringBuilder CommonOptions()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("-o StrictHostKeyChecking=accept-new");
            sb.Append(" -o ConnectTimeout=8");
            sb.Append(" -o NumberOfPasswordPrompts=1");
            sb.Append(" -o ServerAliveInterval=5");
            if (_t.KnownHosts.Length > 0) sb.Append(" -o UserKnownHostsFile=\"").Append(_t.KnownHosts).Append("\"");
            if (_t.KeyFile.Length > 0)
                sb.Append(" -i \"").Append(_t.KeyFile).Append("\" -o IdentitiesOnly=yes -o BatchMode=yes");
            else
                sb.Append(" -o PreferredAuthentications=password,keyboard-interactive");
            if (_t.Port != 22) sb.Append(" -p ").Append(_t.Port);
            sb.Append(" ").Append(_t.User).Append("@").Append(_t.Host);
            return sb;
        }

        /// <summary>远端命令行里绝不能出现 | &amp; &gt; &lt; 双引号 空格，否则会被远端 cmd 拆解
        /// （另外远端 cmd.exe 的命令行上限只有 8191 字符，所以部署要分片）</summary>
        string BuildCmdArguments(string script)
        {
            StringBuilder sb = CommonOptions();
            sb.Append(" powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command ").Append(script);
            return sb.ToString();
        }

        string BuildRunArguments(bool wantInfo)
        {
            StringBuilder sb = CommonOptions();
            if (_agentDeployed) sb.Append(" ").Append(RemoteAgent); else sb.Append(" powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ").Append(RemoteScript);
            sb.Append(_agentDeployed ? " " : " -WantInfo ").Append(wantInfo ? "1" : "0");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- 执行
        string RunSsh(string arguments, int timeoutMs)
        {
            _lastCmd = arguments;
            ProcessStartInfo psi = new ProcessStartInfo("ssh");
            psi.Arguments = arguments;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.EnvironmentVariables["TEMP"] = Config.DataDir;
            psi.EnvironmentVariables["TMP"] = Config.DataDir;
            if (_t.KeyFile.Length == 0 && _t.Password.Length > 0)
            {
                psi.EnvironmentVariables["SSH_ASKPASS"] = Path.Combine(Config.Dir, "ssh-askpass.exe");
                psi.EnvironmentVariables["SSH_ASKPASS_REQUIRE"] = "force";
                psi.EnvironmentVariables["GPM_SSH_PW"] = _t.Password;
            }

            string stdout, stderr;
            using (Process p = Process.Start(psi))
            {
                if (p == null) throw new Exception("无法启动 ssh");
                Task<string> so = p.StandardOutput.ReadToEndAsync();
                Task<string> se = p.StandardError.ReadToEndAsync();
                p.StandardInput.Close();   // 明确给 ssh 一个空 stdin，避免它继承到不该读的句柄
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    throw new Exception("ssh 会话超时（" + (timeoutMs / 1000) + " 秒）");
                }
                stdout = so.Result;
                stderr = se.Result;
                _lastExit = p.ExitCode;
            }
            _lastOut = stdout;
            _lastErr = stderr;
            if (stderr.Trim().Length > 0 && stdout.IndexOf('{') < 0)
                throw new Exception("ssh: " + FirstLine(stderr));
            if (_lastExit != 0 && stdout.IndexOf('{') < 0)
                throw new Exception("ssh 退出码 " + _lastExit + "：" + FirstLine(stderr));
            return stdout;
        }

        void DeployAgent()
        {
            string b64 = Convert.ToBase64String(_agent);
            const int chunk = 7000;   // 远端 cmd.exe 命令行上限 8191 字符
            int n = (b64.Length + chunk - 1) / chunk;
            for (int i = 0; i < n; i++)
            {
                string part = b64.Substring(i * chunk, Math.Min(chunk, b64.Length - i * chunk));
                string script = (i == 0)
                    ? "[void][IO.Directory]::CreateDirectory('" + RemoteDir + "');[IO.File]::WriteAllText('" + RemoteAgentB64 + "','" + part + "')"
                    : "[IO.File]::AppendAllText('" + RemoteAgentB64 + "','" + part + "')";
                RunSsh(BuildCmdArguments(script), 60000);
            }
            // 二进制必须按字节落盘
            string dec = "[IO.File]::WriteAllBytes('" + RemoteAgent + "',[Convert]::FromBase64String([IO.File]::ReadAllText('" + RemoteAgentB64 + "')))";
            RunSsh(BuildCmdArguments(dec), 60000);
            try { RunSsh(BuildCmdArguments("[IO.File]::Delete('" + RemoteAgentB64 + "')"), 60000); } catch { }
        }

        void Deploy()
        {
            if (_agent != null && _agentFails < 2)
            {
                DeployAgent();
                _agentDeployed = true;
                _deployed = true;
                SaveState(true);
                return;
            }
            DeployScript();
            _agentDeployed = false;
            _deployed = true;
            SaveState(true);
        }

        void DeployScript()
        {
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(_payload));
            const int chunk = 6000;                       // 远端 cmd 8191 字符上限的安全取值
            int n = (b64.Length + chunk - 1) / chunk;
            for (int i = 0; i < n; i++)
            {
                string part = b64.Substring(i * chunk, Math.Min(chunk, b64.Length - i * chunk));
                string script = (i == 0)
                    ? "[void][IO.Directory]::CreateDirectory('" + RemoteDir + "');[IO.File]::WriteAllText('" + RemoteB64 + "','" + part + "')"
                    : "[IO.File]::AppendAllText('" + RemoteB64 + "','" + part + "')";
                RunSsh(BuildCmdArguments(script), 60000);
            }
            string decode = "[IO.File]::WriteAllText('" + RemoteScript + "',[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([IO.File]::ReadAllText('" + RemoteB64 + "'))))";
            RunSsh(BuildCmdArguments(decode), 60000);
            _deployed = true;
            SaveState(true);
        }

        // ---------------------------------------------------------------- 采集
        public Snapshot Fetch(bool wantInfo)
        {
            Snapshot s = new Snapshot();
            s.Name = _t.Name;
            s.Channel = _t.Kind=="probe"?"probe":"ssh";
            s.Host = _t.Host;
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                if (_t.Kind != "probe" && !_deployed)
                {
                    if (DeployedBefore())
                    {
                        _deployed = true;
                        _agentDeployed = (_agent != null && _agentFails < 2);
                    }
                    else Deploy();
                }

                string stdout = (_t.Kind == "probe") ? ProbeFetch() : RunSsh(BuildRunArguments(wantInfo), 30000);
                string json = LastJsonLine(stdout);
                if (json == null)
                {
                    _deployed = false;   // 远端脚本可能被清掉了：作废部署记录，下一轮重传
                    SaveState(false);
                    throw new Exception("远端没有返回 JSON：" + FirstLine(_lastErr));
                }

                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = 32 * 1024 * 1024;
                IDictionary<string, object> d = js.DeserializeObject(json) as IDictionary<string, object>;
                if (d == null) throw new Exception("JSON 解析失败");
                string probeErr = Str(d, "error");
                if (probeErr.Length > 0) throw new Exception("探针返回错误: " + probeErr);

                LastDiag = Str(d, "diag");
                s.Host = Str(d, "host");
                s.Os = Str(d, "os");
                s.CpuName = Str(d, "cpu");
                s.GpuName = Str(d, "gpu");
                s.Uptime = Str(d, "uptime");
                s.RamTotalBytes = Num(d, "ramTotal");
                s.CpuPct = Num(d, "cpuPct");
                s.CpuMhz = Num(d, "cpuMhz");
                s.Cores = NumList(d, "cores");
                s.MemTotalBytes = Num(d, "memTotal");
                s.MemAvailBytes = Num(d, "memAvail");
                s.MemCommittedBytes = Num(d, "memCommitted");
                s.MemUsedBytes = (s.MemTotalBytes > 0 && s.MemAvailBytes > 0) ? s.MemTotalBytes - s.MemAvailBytes : 0;
                s.MemPct = (s.MemTotalBytes > 0 && s.MemUsedBytes > 0) ? Math.Min(100, s.MemUsedBytes / s.MemTotalBytes * 100) : 0;
                s.DiskActive = Num(d, "diskActive");
                s.DiskReadBps = Num(d, "diskRead");
                s.DiskWriteBps = Num(d, "diskWrite");
                s.DiskQueue = Num(d, "diskQueue");
                s.NetRxBps = Num(d, "netRx");
                s.NetTxBps = Num(d, "netTx");
                s.NetAdapter = Str(d, "netAdapter");
                s.GpuPct = Num(d, "gpuPct");
                s.Engines = Map(d, "engines");
                s.HasGpu = s.Engines.Count > 0;
                s.VramUsed = Num(d, "vramUsed");
                s.VramShared = Num(d, "vramShared");
                s.GpuTempC = Opt(d, "gpuTemp");
                s.GpuFanRpm = Opt(d, "gpuFan");
                s.GpuPowerPct = Opt(d, "gpuPower");
                s.SysTempC = Opt(d, "sysTemp");
                s.Volumes = Volumes(d, "volumes");
                s.Ok = true;

                if (wantInfo) _lastInfo = s;
                else
                {
                    s.Host = _lastInfo.Host;
                    s.Os = _lastInfo.Os;
                    s.CpuName = _lastInfo.CpuName;
                    s.GpuName = _lastInfo.GpuName;
                    s.Uptime = _lastInfo.Uptime;
                    s.RamTotalBytes = _lastInfo.RamTotalBytes;
                    s.MemTotalBytes = _lastInfo.MemTotalBytes;
                    s.Volumes = _lastInfo.Volumes;
                    // 内存总量来自静态信息，补齐后必须重算已用/百分比
                    s.MemUsedBytes = (s.MemTotalBytes > 0 && s.MemAvailBytes > 0) ? s.MemTotalBytes - s.MemAvailBytes : 0;
                    s.MemPct = (s.MemTotalBytes > 0 && s.MemUsedBytes > 0) ? Math.Min(100, s.MemUsedBytes / s.MemTotalBytes * 100) : 0;
                }
            }
            catch (Exception ex)
            {
                if (_agentDeployed)
                {
                    _agentFails++;
                    if (_agentFails >= 2)
                    {
                        _agentDeployed = false;
                        _deployed = false;
                        SaveState(false);
                    }
                }
                s.Ok = false;
                s.Error = ex.Message;
                try
                {
                    if(_diagnosticError!=s.Error||DateTime.UtcNow-_lastDiagnosticUtc>TimeSpan.FromMinutes(30))
                    {
                    StringBuilder dbg = new StringBuilder();
                    dbg.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + _t.Name + " (" + _t.Host + ")");
                    dbg.AppendLine("cmd: ssh " + _lastCmd);
                    dbg.AppendLine("err: " + ex.Message);
                    dbg.AppendLine("ssh 退出码: " + _lastExit + "   deployed=" + _deployed);
                    dbg.AppendLine("--- stdout (" + _lastOut.Length + " chars) ---");
                    dbg.AppendLine(_lastOut.Length > 4000 ? _lastOut.Substring(0, 4000) : _lastOut);
                    dbg.AppendLine("--- stderr (" + _lastErr.Length + " chars) ---");
                    dbg.AppendLine(_lastErr.Length > 4000 ? _lastErr.Substring(0, 4000) : _lastErr);
                    LocalFiles.WriteDiagnostic(Path.Combine(Config.DataDir, "ssh-error.txt"), dbg.ToString());
                    _diagnosticError=s.Error;_lastDiagnosticUtc=DateTime.UtcNow;
                    }
                }
                catch { }
            }
            sw.Stop();
            s.CollectMs = (int)sw.ElapsedMilliseconds;
            s.At = DateTime.Now;
            if(_connectionOk!=s.Ok){Program.RuntimeLog("connection "+_t.Name+" host="+_t.Host+" channel="+s.Channel+" ok="+s.Ok+(s.Ok?"":" error="+s.Error));_connectionOk=s.Ok;}
            if(s.Ok)_diagnosticError="";
            return s;
        }

        Snapshot _lastInfo = new Snapshot();

        /// <summary>从常驻探针的 TCP 端点取一行 JSON（先报令牌）</summary>
        string ProbeFetch()
        {
            using (System.Net.Sockets.TcpClient c = new System.Net.Sockets.TcpClient())
            {
                c.ReceiveTimeout = 5000;
                c.SendTimeout = 5000;
                _lastOut="";_lastErr="";_lastCmd="";
                var pending=c.BeginConnect(_t.Host,_t.Port,null,null);
                using(var wait=pending.AsyncWaitHandle){if(!wait.WaitOne(5000))throw new TimeoutException("探针连接超时（5 秒）");c.EndConnect(pending);}
                using (System.Net.Sockets.NetworkStream ns = c.GetStream())
                {
                    ns.ReadTimeout = 5000;
                    ns.WriteTimeout = 5000;
                    byte[] b = Encoding.ASCII.GetBytes(_t.Token + "\n");
                    ns.Write(b, 0, b.Length);
                    ns.Flush();
                    byte[] buf = new byte[16384];
                    string s;
                    var deadline=Stopwatch.StartNew();
                    using(var payload=new MemoryStream())
                    {
                        while(true)
                        {
                            int remaining=5000-(int)deadline.ElapsedMilliseconds;if(remaining<=0)throw new TimeoutException("探针响应超时（5 秒）");ns.ReadTimeout=remaining;
                            int n=ns.Read(buf,0,buf.Length);if(n==0)break;
                            int newline=Array.IndexOf(buf,(byte)10,0,n);int count=newline>=0?newline+1:n;
                            if(payload.Length+count>65536)throw new IOException("探针响应超过大小限制");payload.Write(buf,0,count);if(newline>=0)break;
                        }
                        s=Encoding.UTF8.GetString(payload.ToArray());
                    }
                    _lastOut = s; _lastErr = ""; _lastExit = 0;
                    return s;
                }
            }
        }

        static string FirstLine(string t)
        {
            if (string.IsNullOrEmpty(t)) return "";
            string[] lines = t.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string l = lines[i].Trim();
                if (l.Length > 0) return l.Length > 300 ? l.Substring(0, 300) : l;
            }
            return "";
        }

        static string LastJsonLine(string t)
        {
            if (string.IsNullOrEmpty(t)) return null;
            string[] lines = t.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string l = lines[i].Trim();
                if (l.StartsWith("{") && l.EndsWith("}")) return l;
            }
            return null;
        }

        static string Str(IDictionary<string, object> d, string k)
        {
            object o;
            if (d != null && d.TryGetValue(k, out o) && o != null) return Convert.ToString(o);
            return "";
        }

        static double Num(IDictionary<string, object> d, string k)
        {
            double v = Opt(d, k);
            return double.IsNaN(v) ? 0 : v;
        }

        static double Opt(IDictionary<string, object> d, string k)
        {
            object o;
            if (d != null && d.TryGetValue(k, out o) && o != null)
            {
                try { return Convert.ToDouble(o); }
                catch { return double.NaN; }
            }
            return double.NaN;
        }

        static List<double> NumList(IDictionary<string, object> d, string k)
        {
            List<double> r = new List<double>();
            object o;
            if (d != null && d.TryGetValue(k, out o) && o != null)
            {
                System.Collections.IEnumerable en = o as System.Collections.IEnumerable;
                if (en != null)
                {
                    foreach (object item in en)
                    {
                        try { r.Add(Convert.ToDouble(item)); }
                        catch { r.Add(0); }
                    }
                }
            }
            return r;
        }

        static Dictionary<string, double> Map(IDictionary<string, object> d, string k)
        {
            Dictionary<string, double> r = new Dictionary<string, double>();
            object o;
            if (d != null && d.TryGetValue(k, out o) && o != null)
            {
                IDictionary<string, object> m = o as IDictionary<string, object>;
                if (m != null)
                {
                    foreach (KeyValuePair<string, object> kv in m)
                    {
                        try { r[kv.Key] = Convert.ToDouble(kv.Value); }
                        catch { }
                    }
                }
            }
            return r;
        }

        static List<Volume> Volumes(IDictionary<string, object> d, string k)
        {
            List<Volume> r = new List<Volume>();
            object o;
            if (d != null && d.TryGetValue(k, out o) && o != null)
            {
                System.Collections.IEnumerable en = o as System.Collections.IEnumerable;
                if (en != null)
                {
                    foreach (object item in en)
                    {
                        IDictionary<string, object> m = item as IDictionary<string, object>;
                        if (m == null) continue;
                        Volume v = new Volume();
                        v.Drive = Str(m, "d");
                        v.SizeBytes = Num(m, "size");
                        v.FreeBytes = Num(m, "free");
                        if (v.Drive.Length > 0) r.Add(v);
                    }
                }
            }
            return r;
        }
    }
}
