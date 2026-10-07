using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Gpm
{
    static class Program
    {
        static Mutex _instance;
        static readonly object _logGate = new object();
        static System.Threading.Timer _heartbeat;
        static int _processId;
        static List<Target> _samplingTargets;
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError=true)]
        static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool inJob);
        internal static void RuntimeLog(string message)
        {
            try
            {
                lock (_logGate)
                {
                    string dir = String.IsNullOrEmpty(Config.DataDir) ? Config.Dir : Config.DataDir;
                    string path = Path.Combine(dir, "runtime.log");
                    LocalFiles.Append(path, DateTimeOffset.Now.ToString("o") + " pid=" + _processId + " " + message + Environment.NewLine,262144);
                }
            }
            catch { }
        }
        static void LogException(string origin, Exception error)
        {
            string detail;
            try { detail = error.ToString(); }
            catch { detail = error.GetType().FullName + ": " + error.Message; }
            RuntimeLog(origin + Environment.NewLine + detail);
        }
        static void StartDiagnostics()
        {
            LocalFiles.Cleanup(Config.DataDir,DateTime.UtcNow);if(Config.Dir!=Config.DataDir)LocalFiles.Cleanup(Config.Dir,DateTime.UtcNow);
            using (Process process = Process.GetCurrentProcess())
            {
                _processId = process.Id;
                bool inJob;
                string job = IsProcessInJob(process.Handle, IntPtr.Zero, out inJob) ? inJob.ToString() : "unknown";
                RuntimeLog("start session=" + process.SessionId + " inJob=" + job + " versionUtc=" + File.GetLastWriteTimeUtc(Application.ExecutablePath).ToString("o"));
            }
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                var error = e.ExceptionObject as Exception;
                if (error != null) LogException("unhandled terminating=" + e.IsTerminating + " thread=" + Thread.CurrentThread.Name, error);
                else RuntimeLog("unhandled terminating=" + e.IsTerminating);
            };
            AppDomain.CurrentDomain.ProcessExit += delegate { RuntimeLog("process exit code=" + Environment.ExitCode); };
            Application.ApplicationExit += delegate { RuntimeLog("application exit"); };
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                LogException("UI thread exception", e.Exception);
                if (!Quiet)
                {
                    using (var dialog = new ThreadExceptionDialog(e.Exception))
                        if (dialog.ShowDialog() == DialogResult.Abort) Application.Exit();
                }
            };
            Microsoft.Win32.SystemEvents.SessionEnding += delegate(object sender, Microsoft.Win32.SessionEndingEventArgs e) { RuntimeLog("session ending reason=" + e.Reason); };
            Microsoft.Win32.SystemEvents.SessionEnded += delegate(object sender, Microsoft.Win32.SessionEndedEventArgs e) { RuntimeLog("session ended reason=" + e.Reason); };
            Microsoft.Win32.SystemEvents.PowerModeChanged += delegate(object sender, Microsoft.Win32.PowerModeChangedEventArgs e) { RuntimeLog("power mode=" + e.Mode); };
            DateTime lastHeartbeat=DateTime.MinValue,lastCleanup=DateTime.UtcNow;
            _heartbeat = new System.Threading.Timer(delegate
            {
                if(DateTime.UtcNow-lastCleanup>TimeSpan.FromHours(1)){LocalFiles.Cleanup(Config.DataDir,DateTime.UtcNow);if(Config.Dir!=Config.DataDir)LocalFiles.Cleanup(Config.Dir,DateTime.UtcNow);lastCleanup=DateTime.UtcNow;}
                if(DateTime.UtcNow-lastHeartbeat<TimeSpan.FromMinutes(5))return;
                lastHeartbeat=DateTime.UtcNow;
                RuntimeLog("alive");
                var targets = _samplingTargets;
                if (targets == null) return;
                foreach (var target in targets)
                {
                    if (!target.Enabled) continue;
                    var sample = target.Latest;
                    RuntimeLog("sampler " + target.Name + " host=" + target.Host +
                        (sample == null ? " awaiting first sample" : " ok=" + sample.Ok + " ageSeconds=" + Math.Max(0,(DateTime.Now-sample.At).TotalSeconds).ToString("0") + (sample.Ok ? "" : " error=" + sample.Error)));
                }
            }, null, 30000, 30000);
        }
        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                Config.Dir = AppDomain.CurrentDomain.BaseDirectory;
                Config.DataDir = Config.Dir;
                Run(args);
            }
            catch (Exception ex)
            {
                Fatal(ex);
            }
            finally
            {
                if(_processId!=0)RuntimeLog("main returned");
                if (_heartbeat != null) _heartbeat.Dispose();
                if (_instance != null) { _instance.ReleaseMutex(); _instance.Dispose(); }
            }
        }

        /// <summary>出错时先写日志文件（GUI 进程在无交互桌面时 MessageBox 会静默失败）</summary>
        static void Fatal(Exception ex)
        {
            LogException("fatal", ex);
            try
            {
                string p = Path.Combine(Config.DataDir, "startup-error.txt");
                LocalFiles.WriteDiagnostic(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" + ex.ToString());
            }
            catch { }
            if (Quiet) return;   // 报告/截图模式下绝不弹窗，否则会挂在无人点击的对话框上
            try { MessageBox.Show(ex.Message, "GPU PC 性能监视 - 启动失败"); }
            catch { }
        }

        static bool Quiet;

        static void Run(string[] args)
        {
            string cfg = null, shot = null, report = null, data = null, shotSettings = null;
            bool sshtest = false, wantShot = false, wantReport = false, all = false, wantShotSettings = false;
            int seconds = 8;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--config" && i + 1 < args.Length) cfg = args[++i];
                else if (args[i] == "--shot") { wantShot = true; if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) shot = args[++i]; }
                else if (args[i] == "--report") { wantReport = true; if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) report = args[++i]; }
                else if (args[i] == "--data" && i + 1 < args.Length) data = args[++i];
                else if (args[i] == "--all") all = true;
                else if (args[i] == "--shot-settings") { wantShotSettings = true; if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) shotSettings = args[++i]; }
                else if (args[i] == "--sshtest") sshtest = true;
                else if (args[i] == "--seconds" && i + 1 < args.Length) seconds = int.Parse(args[++i]);
            }
            Quiet = (wantReport || wantShot || sshtest || wantShotSettings);

            if (cfg == null) cfg = Path.Combine(Config.Dir, "config.json");

            List<Target> targets;
            try
            {
                targets = Config.Load(cfg);
            }
            catch (Exception ex)
            {
                Fatal(new Exception("读取配置失败：" + cfg, ex));
                return;
            }
            if (targets.Count == 0)
            {
                Fatal(new Exception("config.json 里没有任何目标。"));
                return;
            }
            // 命令行 --data 优先于 config.json 的 dataDir（用来把运行期产物全放到空间大的盘）
            if (data != null)
            {
                try
                {
                    Config.DataDir = Path.GetFullPath(data);
                    if (!Directory.Exists(Config.DataDir)) Directory.CreateDirectory(Config.DataDir);
                }
                catch (Exception ex)
                {
                    Fatal(new Exception("无法使用数据目录：" + data, ex));
                    return;
                }
            }

            // --all：强制打开所有目标（排错/截图用，不写回 config.json）
            if (all) foreach (Target t in targets) t.Enabled = true;

            if (wantReport)
            {
                if (report == null) report = Path.Combine(Config.DataDir, "report.txt");
                DoReport(targets, report, all);
                return;
            }            if (sshtest)
            {
                DoSshTest(targets, Path.Combine(Config.DataDir, "sshtest.txt"));
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (wantShotSettings)
            {
                SettingsForm sf = new SettingsForm(targets, cfg);
                sf.Show();
                DateTime end2 = DateTime.Now.AddSeconds(seconds);
                while (DateTime.Now < end2) { Application.DoEvents(); Thread.Sleep(70); }
                using (Bitmap sb = new Bitmap(sf.ClientSize.Width, sf.ClientSize.Height))
                {
                    sf.DrawToBitmap(sb, new Rectangle(0, 0, sb.Width, sb.Height));
                    sb.Save(shotSettings != null ? shotSettings : Path.Combine(Config.DataDir, "shot-settings.png"), ImageFormat.Png);
                }
                sf.Close();
                return;
            }
            if (!wantShot)
            {
                bool first;
                Mutex candidate = new Mutex(true, "Local\\GPUPCMGT.Monitor", out first);
                if (!first) { candidate.Dispose(); SingleInstance.ActivateExisting(); return; }
                _instance = candidate;
            }
            if (!Quiet) StartDiagnostics();
            MainForm f = new MainForm(targets, cfg, all);
            f.RestoreWidgetOnShow=!Quiet;
            StartSamplers(targets);

            if (wantShot)
            {
                if (shot == null) shot = Path.Combine(Config.DataDir, "shot.png");
                f.Show();
                DateTime end = DateTime.Now.AddSeconds(seconds);
                while (DateTime.Now < end)
                {
                    Application.DoEvents();
                    Thread.Sleep(70);
                }
                try
                {
                    using (Bitmap bmp = new Bitmap(f.ClientSize.Width, f.ClientSize.Height))
                    {
                        f.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                        bmp.Save(shot, ImageFormat.Png);
                    }
                }
                catch (Exception ex)
                {
                    File.WriteAllText(shot + ".err.txt", ex.ToString());
                }
                f.ExitApplication();
                return;
            }

            Application.Run(f);
        }

        static void StartSamplers(List<Target> targets)
        {
            _samplingTargets = targets;
            foreach (Target t in targets)
            {
                Target tt = t;
                Thread th = new Thread(delegate()
                {
                    if (tt.Kind == "local")
                    {
                        LocalSampler ls = null;
                        while (true)
                        {
                            // 未勾选"包含本机"时完全不采样，连 PDH 查询都不建
                            if (!tt.Enabled) { Thread.Sleep(1000); continue; }
                            if (ls == null) { ls = new LocalSampler(); ls.Prime(); }
                            Snapshot s = ls.Sample(1000);
                            s.Name = tt.Name;
                            tt.Latest = s;
                            FaultHistory.Observe(tt,s);
                            Thread.Sleep(Math.Max(500, tt.IntervalSec * 1000));
                        }
                    }
                    else
                    {
                        RemoteSampler rs = new RemoteSampler(tt);
                        DateTime lastInfo = DateTime.MinValue;
                        bool first = true;
                        while (true)
                        {
                            if (!tt.Enabled) { Thread.Sleep(1000); continue; }
                            bool wantInfo = first || (DateTime.Now - lastInfo).TotalMinutes >= 5;
                            Snapshot s = rs.Fetch(wantInfo);
                            if (wantInfo && s.Ok) lastInfo = DateTime.Now;
                            s.Name = tt.Name;
                            tt.Latest = s;
                            FaultHistory.Observe(tt,s);
                            first = false;
                            // 远程一轮本身就 3~4 秒，间隔要扣掉采集耗时，否则实际周期会翻倍
                            // 空闲自适应：机器闲着就少去打扰它（省 CPU、省 SSH 登录、省写盘）
                            bool busy = s.Ok && (s.CpuPct >= 8 || s.GpuPct >= 8 || s.DiskActive >= 10);
                            int interval = tt.IntervalSec;
                            if (!busy && tt.IdleIntervalSec > 0) interval = tt.IdleIntervalSec;
                            int wait = Math.Max(200, interval * 1000 - s.CollectMs);
                            Thread.Sleep(wait);
                        }
                    }
                });
                th.IsBackground = true;
                th.Name = "sampler:" + tt.Name;
                th.Start();
            }
        }

        /// <summary>ssh 通道自检：逐级验证认证、stdin 管道、远端 PowerShell，最后跑一次完整采集</summary>
        static void DoSshTest(List<Target> targets, string path)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("ssh 通道自检 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            foreach (Target t in targets)
            {
                if (t.Kind == "local") continue;
                sb.AppendLine("=== " + t.Name + "  " + t.User + "@" + t.Host + ":" + t.Port + " ===");
                sb.AppendLine("knownHosts=" + (t.KnownHosts.Length > 0 ? t.KnownHosts : "(默认)") + "  askpass=" + Path.Combine(Config.Dir, "ssh-askpass.exe"));
                RunSsh(t, "whoami", null, sb);
                RunSsh(t, "powershell -NoProfile -NonInteractive -Command -", "Write-Output ('PING-' + $env:COMPUTERNAME)", sb);
                RunSsh(t, "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -", "(1..2 | ForEach-Object { 'L' + $_ })", sb);
                RemoteSampler rs = new RemoteSampler(t);
                Snapshot s = rs.Fetch(true);
                sb.AppendLine("--- 完整采集 ---");
                sb.AppendLine("   ok=" + s.Ok + " 用时=" + s.CollectMs + "ms err=" + s.Error);
                sb.AppendLine("   gpuTemp=" + s.GpuTempC + " cpuPct=" + s.CpuPct + " cores=" + s.Cores.Count + " os=" + s.Os);
                sb.AppendLine("   diag=" + rs.LastDiag);
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        static void RunSsh(Target t, string remoteCmd, string payload, StringBuilder sb)
        {
            StringBuilder a = new StringBuilder();
            a.Append("-o StrictHostKeyChecking=accept-new -o ConnectTimeout=8 -o NumberOfPasswordPrompts=1");
            if (t.KnownHosts.Length > 0) a.Append(" -o UserKnownHostsFile=\"").Append(t.KnownHosts).Append("\"");
            a.Append(" -o PreferredAuthentications=password,keyboard-interactive");
            a.Append(" ").Append(t.User).Append("@").Append(t.Host);
            a.Append(" \"").Append(remoteCmd).Append("\"");
            sb.AppendLine("--- ssh " + remoteCmd + (payload != null ? "  [+stdin " + payload.Length + " 字符]" : "") + " ---");
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("ssh");
                psi.Arguments = a.ToString();
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                psi.EnvironmentVariables["SSH_ASKPASS"] = Path.Combine(Config.Dir, "ssh-askpass.exe");
                psi.EnvironmentVariables["SSH_ASKPASS_REQUIRE"] = "force";
                psi.EnvironmentVariables["GPM_SSH_PW"] = t.Password;
                using (Process p = Process.Start(psi))
                {
                    System.Threading.Tasks.Task<string> so = p.StandardOutput.ReadToEndAsync();
                    System.Threading.Tasks.Task<string> se = p.StandardError.ReadToEndAsync();
                    if (payload != null) p.StandardInput.Write(payload);
                    p.StandardInput.Close();
                    if (!p.WaitForExit(30000))
                    {
                        try { p.Kill(); } catch { }
                        sb.AppendLine("   超时(30s)");
                        return;
                    }
                    sb.AppendLine("   退出码=" + p.ExitCode);
                    sb.AppendLine("   stdout=[" + so.Result.Trim() + "]");
                    sb.AppendLine("   stderr=[" + se.Result.Trim() + "]");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("   异常: " + ex.ToString());
            }
        }

        static void DoReport(List<Target> targets, string path, bool all)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("GPU_PC_MGT 采样自检  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();
            foreach (Target t in targets)
            {
                if (!t.Enabled && !all) continue;
                sb.AppendLine("=== " + t.Name + "  (" + t.Kind + (t.Host.Length > 0 ? " " + t.User + "@" + t.Host : "") + ") ===");
                if (t.Kind == "local")
                {
                    using (LocalSampler ls = new LocalSampler())
                    {
                        ls.Prime();
                        for (int i = 0; i < 3; i++)
                        {
                            Thread.Sleep(1000);
                            sb.AppendLine(Describe(ls.Sample(1000)));
                        }
                    }
                }
                else
                {
                    RemoteSampler rs = new RemoteSampler(t);
                    for (int i = 0; i < 2; i++)
                    {
                        Snapshot s = rs.Fetch(i == 0);
                        sb.AppendLine(Describe(s));
                        sb.AppendLine("    diag: " + rs.LastDiag);
                        Thread.Sleep(1200);
                    }
                }
                sb.AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        static string Describe(Snapshot s)
        {
            StringBuilder b = new StringBuilder();
            b.AppendLine("  ok=" + s.Ok + " 用时=" + s.CollectMs + "ms " + s.Error);
            b.AppendLine("  主机=" + s.Host + "  OS=" + s.Os + "  运行=" + s.Uptime);
            b.AppendLine("  CPU=" + s.CpuName);
            b.AppendLine("  GPU=" + s.GpuName);
            b.AppendLine("  CPU占用=" + s.CpuPct.ToString("0.0") + "%  " + s.CpuMhz.ToString("0") + "MHz  逻辑处理器=" + s.Cores.Count
                + "  每核=" + Join(s.Cores));
            b.AppendLine("  内存=" + MB(s.MemUsedBytes) + "/" + MB(s.MemTotalBytes) + "MB (" + s.MemPct.ToString("0.0") + "%) 可用=" + MB(s.MemAvailBytes)
                + "MB 已提交=" + MB(s.MemCommittedBytes) + "MB");
            b.AppendLine("  磁盘活动=" + s.DiskActive.ToString("0.0") + "% 读=" + MB(s.DiskReadBps) + "MB/s 写=" + MB(s.DiskWriteBps) + "MB/s 队列=" + s.DiskQueue.ToString("0.00"));
            b.AppendLine("  网络↓=" + (s.NetRxBps / 1024).ToString("0") + "KB/s ↑=" + (s.NetTxBps / 1024).ToString("0") + "KB/s [" + s.NetAdapter + "]");
            b.Append("  GPU占用=" + s.GpuPct.ToString("0.0") + "%  引擎=");
            foreach (KeyValuePair<string, double> kv in s.Engines) b.Append(kv.Key + "=" + kv.Value.ToString("0.0") + " ");
            b.AppendLine();
            b.AppendLine("  显存专用=" + MB(s.VramUsed) + "MB 共享=" + MB(s.VramShared) + "MB");
            b.AppendLine("  GPU温度=" + N(s.GpuTempC) + "  风扇=" + N(s.GpuFanRpm) + "  功耗=" + N(s.GpuPowerPct) + "%TDP  系统热区=" + N(s.SysTempC));
            b.Append("  卷=");
            foreach (Volume v in s.Volumes) b.Append(v.Drive + " " + MB(v.FreeBytes) + "/" + MB(v.SizeBytes) + "MB  ");
            b.AppendLine();
            return b.ToString();
        }

        static string Join(List<double> v)
        {
            StringBuilder b = new StringBuilder();
            for (int i = 0; i < v.Count && i < 40; i++) b.Append(v[i].ToString("0")).Append(' ');
            return b.ToString();
        }

        static string MB(double b) { return (b / 1048576.0).ToString("0"); }
        static string N(double d) { return double.IsNaN(d) ? "-" : d.ToString("0.0"); }
    }
}
