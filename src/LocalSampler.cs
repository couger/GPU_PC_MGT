using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Win32;

namespace Gpm
{
    /// <summary>本机采集：原生 PDH 持久查询 + D3DKMT GPU 传感器 + 注册表/DriveInfo 静态信息</summary>
    public class LocalSampler : IDisposable
    {
        enum Kind
        {
            CpuUtil, CpuFreq, MemAvailMB, MemCommitted,
            DiskRead, DiskWrite, DiskIdle, DiskQueue,
            LogicalFree, NetRx, NetTx,
            GpuEngine, GpuMemDed, GpuMemShared, ThermalK
        }

        class Ctr
        {
            public string Path;
            public Kind Kind;
            public IntPtr Handle;
        }

        IntPtr _query = IntPtr.Zero;
        readonly List<Ctr> _ctrs = new List<Ctr>();
        bool _hasUtility;
        readonly Snapshot _info = new Snapshot();
        bool _opened;
        long _lastGpuMs;

        public LocalSampler()
        {
            try
            {
                IntPtr q;
                uint st = Native.PdhOpenQuery(null, IntPtr.Zero, out q);
                if (st == 0)
                {
                    _query = q;
                    _opened = true;
                }
            }
            catch { }

            Add(@"\Processor Information(*)\% Processor Utility", Kind.CpuUtil);
            Add(@"\Processor Information(*)\% Processor Time", Kind.CpuUtil);      // 老系统兜底
            Add(@"\Processor Information(_Total)\Processor Frequency", Kind.CpuFreq);
            Add(@"\Memory\Available MBytes", Kind.MemAvailMB);
            Add(@"\Memory\Committed Bytes", Kind.MemCommitted);
            Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec", Kind.DiskRead);
            Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec", Kind.DiskWrite);
            Add(@"\PhysicalDisk(_Total)\% Idle Time", Kind.DiskIdle);
            Add(@"\PhysicalDisk(_Total)\Avg. Disk Queue Length", Kind.DiskQueue);
            Add(@"\LogicalDisk(*)\Free Megabytes", Kind.LogicalFree);
            Add(@"\Network Interface(*)\Bytes Received/sec", Kind.NetRx);
            Add(@"\Network Interface(*)\Bytes Sent/sec", Kind.NetTx);
            Add(@"\GPU Engine(*)\Utilization Percentage", Kind.GpuEngine);
            Add(@"\GPU Adapter Memory(*)\Dedicated Usage", Kind.GpuMemDed);
            Add(@"\GPU Adapter Memory(*)\Shared Usage", Kind.GpuMemShared);
            Add(@"\Thermal Zone Information(*)\Temperature", Kind.ThermalK);

            LoadStaticInfo();
        }

        void Add(string path, Kind kind)
        {
            if (!_opened) return;
            try
            {
                IntPtr h;
                if (Native.PdhAddEnglishCounter(_query, path, IntPtr.Zero, out h) == 0)
                {
                    Ctr c = new Ctr();
                    c.Path = path; c.Kind = kind; c.Handle = h;
                    _ctrs.Add(c);
                    if (path.IndexOf("Utility", StringComparison.OrdinalIgnoreCase) >= 0) _hasUtility = true;
                }
            }
            catch { }
        }

        // ============ 静态信息（启动时取一次）============
        void LoadStaticInfo()
        {
            _info.Host = Environment.MachineName;
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k != null)
                    {
                        string pn = k.GetValue("ProductName") as string;
                        string dv = k.GetValue("DisplayVersion") as string;
                        object cb = k.GetValue("CurrentBuild");
                        _info.Os = ((pn ?? "") + " " + (dv ?? "") + " (build " + (cb == null ? "?" : cb.ToString()) + ")").Trim();
                    }
                }
            }
            catch { }
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    if (k != null) _info.CpuName = ((k.GetValue("ProcessorNameString") as string) ?? "").Trim();
                }
            }
            catch { }
            _info.GpuName = ReadGpuNames();

            double total, avail;
            Native.GetMemory(out total, out avail);
            _info.RamTotalBytes = total;

            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    Volume v = new Volume();
                    v.Drive = d.Name.TrimEnd('\\');
                    v.SizeBytes = d.TotalSize;
                    v.FreeBytes = d.TotalFreeSpace;
                    _info.Volumes.Add(v);
                }
            }
            catch { }
        }

        static string ReadGpuNames()
        {
            List<string> names = new List<string>();
            try
            {
                using (RegistryKey cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (cls != null)
                    {
                        foreach (string sub in cls.GetSubKeyNames())
                        {
                            if (sub.Length != 4) continue;
                            using (RegistryKey k = cls.OpenSubKey(sub))
                            {
                                if (k == null) continue;
                                string desc = k.GetValue("DriverDesc") as string;
                                if (string.IsNullOrEmpty(desc)) continue;
                                if (desc.IndexOf("Remote Display", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                                if (desc.IndexOf("Basic Display", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                                if (desc.IndexOf("Basic Render", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                                if (!names.Contains(desc)) names.Add(desc);
                            }
                        }
                    }
                }
            }
            catch { }
            return string.Join(" / ", names.ToArray());
        }

        // ============ 采样 ============
        /// <summary>第一次采集（PDH 速率类计数器需要两次采样）</summary>
        public void Prime()
        {
            try { if (_opened) Native.PdhCollectQueryData(_query); } catch { }
            _lastGpuMs = 0;
        }

        public Snapshot Sample(int gpuEveryMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            Snapshot s = new Snapshot();
            s.Ok = true;
            s.Name = "本机";
            s.Channel = "local";
            s.Host = _info.Host;
            s.Os = _info.Os;
            s.CpuName = _info.CpuName;
            s.GpuName = _info.GpuName;
            s.RamTotalBytes = _info.RamTotalBytes;
            s.Volumes = _info.Volumes;

            TimeSpan up = Native.Uptime();
            s.Uptime = string.Format("{0}天{1:00}:{2:00}", up.Days, up.Hours, up.Minutes);

            try
            {
                if (_opened) Native.PdhCollectQueryData(_query);

                List<double> coresUtil = new List<double>();
                List<double> coresTime = new List<double>();
                Dictionary<string, double> gpuEngines = new Dictionary<string, double>();
                double vramDed = 0, vramShared = 0, thermalMaxK = 0;
                double rxSum = 0, txSum = 0, rxBest = -1;
                string bestNic = "";
                Dictionary<string, double> diskFree = new Dictionary<string, double>();

                for (int i = 0; i < _ctrs.Count; i++)
                {
                    Ctr c = _ctrs[i];
                    Dictionary<string, double> vals = ReadArray(c.Handle);
                    if (vals.Count == 0) continue;

                    switch (c.Kind)
                    {
                        case Kind.CpuUtil:
                            {
                                bool isUtility = c.Path.IndexOf("Utility", StringComparison.OrdinalIgnoreCase) >= 0;
                                double plainTotal = double.NaN, groupTotal = double.NaN;
                                foreach (KeyValuePair<string, double> kv in vals)
                                {
                                    string n = kv.Key.ToLowerInvariant();
                                    if (n == "_total") { plainTotal = kv.Value; continue; }
                                    if (n.EndsWith("_total")) { groupTotal = kv.Value; continue; }
                                    if (n.Length == 0) continue;
                                    if (isUtility) { if (coresUtil.Count < 256) coresUtil.Add(kv.Value); }
                                    else { if (coresTime.Count < 256) coresTime.Add(kv.Value); }
                                }
                                if (isUtility || !_hasUtility)
                                {
                                    if (!double.IsNaN(plainTotal)) s.CpuPct = plainTotal;
                                    else if (!double.IsNaN(groupTotal)) s.CpuPct = groupTotal;
                                }
                            }
                            break;

                        case Kind.CpuFreq:
                            foreach (KeyValuePair<string, double> kv in vals) { if (kv.Value > s.CpuMhz) s.CpuMhz = kv.Value; }
                            break;

                        case Kind.MemAvailMB:
                            foreach (KeyValuePair<string, double> kv in vals) s.MemAvailBytes = kv.Value * 1048576.0;
                            break;

                        case Kind.MemCommitted:
                            foreach (KeyValuePair<string, double> kv in vals) s.MemCommittedBytes = kv.Value;
                            break;

                        case Kind.DiskRead:
                            foreach (KeyValuePair<string, double> kv in vals) s.DiskReadBps += kv.Value;
                            break;

                        case Kind.DiskWrite:
                            foreach (KeyValuePair<string, double> kv in vals) s.DiskWriteBps += kv.Value;
                            break;

                        case Kind.DiskIdle:
                            foreach (KeyValuePair<string, double> kv in vals) s.DiskActive = Math.Max(0, Math.Min(100, 100.0 - kv.Value));
                            break;

                        case Kind.DiskQueue:
                            foreach (KeyValuePair<string, double> kv in vals) s.DiskQueue = kv.Value;
                            break;

                        case Kind.LogicalFree:
                            foreach (KeyValuePair<string, double> kv in vals) diskFree[kv.Key] = kv.Value;
                            break;

                        case Kind.NetRx:
                        case Kind.NetTx:
                            foreach (KeyValuePair<string, double> kv in vals)
                            {
                                string n = kv.Key.ToLowerInvariant();
                                if (n.IndexOf("loopback") >= 0 || n.IndexOf("isatap") >= 0 || n.IndexOf("teredo") >= 0
                                    || n.IndexOf("pseudo") >= 0 || n.IndexOf("bluetooth") >= 0 || n.IndexOf("_total") >= 0) continue;
                                if (c.Kind == Kind.NetRx) rxSum += kv.Value; else txSum += kv.Value;
                                if (c.Kind == Kind.NetRx && kv.Value > rxBest) { rxBest = kv.Value; bestNic = kv.Key; }
                            }
                            break;

                        case Kind.GpuEngine:
                            foreach (KeyValuePair<string, double> kv in vals)
                            {
                                string key = EngineKey(kv.Key);
                                if (key == null) continue;
                                double cur;
                                if (!gpuEngines.TryGetValue(key, out cur)) cur = 0;
                                gpuEngines[key] = Math.Min(100.0, cur + kv.Value);
                            }
                            break;

                        case Kind.GpuMemDed:
                            foreach (KeyValuePair<string, double> kv in vals) { if (kv.Value > vramDed) vramDed = kv.Value; }
                            break;

                        case Kind.GpuMemShared:
                            foreach (KeyValuePair<string, double> kv in vals) { if (kv.Value > vramShared) vramShared = kv.Value; }
                            break;

                        case Kind.ThermalK:
                            foreach (KeyValuePair<string, double> kv in vals)
                            {
                                if (kv.Value > 250 && kv.Value < 400 && kv.Value > thermalMaxK) thermalMaxK = kv.Value;
                            }
                            break;
                    }
                }

                s.Cores = coresUtil.Count > 0 ? coresUtil : coresTime;
                s.MemTotalBytes = s.RamTotalBytes;
                double used = s.RamTotalBytes - s.MemAvailBytes;
                s.MemUsedBytes = used > 0 ? used : 0;
                s.MemPct = s.RamTotalBytes > 0 ? Math.Min(100.0, s.MemUsedBytes / s.RamTotalBytes * 100.0) : 0;

                // 卷：用驱动器信息的总容量 + 计数器的实时可用空间
                for (int i = 0; i < s.Volumes.Count; i++)
                {
                    Volume v = s.Volumes[i];
                    double freeMB;
                    if (diskFree.TryGetValue(v.Drive.ToLowerInvariant(), out freeMB)) v.FreeBytes = freeMB * 1048576.0;
                }
                for (int i = s.Volumes.Count - 1; i >= 0; i--)
                {
                    Volume v = s.Volumes[i];
                    if (v.SizeBytes <= 0) s.Volumes.RemoveAt(i);
                }

                s.NetRxBps = rxSum; s.NetTxBps = txSum;
                s.NetAdapter = ShortNic(bestNic);
                s.Engines = gpuEngines;
                s.HasGpu = gpuEngines.Count > 0;
                s.VramUsed = vramDed;
                s.VramShared = vramShared;
                foreach (KeyValuePair<string, double> kv in gpuEngines) { if (kv.Value > s.GpuPct) s.GpuPct = kv.Value; }
                if (thermalMaxK > 0) s.SysTempC = thermalMaxK - 273.15;

                // GPU 传感器按需采样（默认每轮都采，开销极小）
                long now = sw.ElapsedMilliseconds;
                double t, fan, pw;
                Native.ReadGpuPerf(out t, out fan, out pw);
                s.GpuTempC = t; s.GpuFanRpm = fan; s.GpuPowerPct = pw;
                _lastGpuMs = now;
            }
            catch (Exception ex)
            {
                s.Ok = false;
                s.Error = ex.Message;
            }

            sw.Stop();
            s.CollectMs = (int)sw.ElapsedMilliseconds;
            s.At = DateTime.Now;
            return s;
        }

        static string EngineKey(string instance)
        {
            int i = instance.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            string k = instance.Substring(i + 8);
            int h = k.IndexOf('#');
            if (h >= 0) k = k.Substring(0, h);
            k = k.Trim();
            while (k.Length > 0 && (char.IsDigit(k[k.Length - 1]) || k[k.Length - 1] == ' ')) k = k.Substring(0, k.Length - 1);
            return k.Trim().ToLowerInvariant();
        }

        static string ShortNic(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            int i = name.IndexOf('(');
            int j = name.LastIndexOf(')');
            if (i >= 0 && j > i) return name.Substring(i + 1, j - i - 1);
            return name;
        }

        static Dictionary<string, double> ReadArray(IntPtr counter)
        {
            Dictionary<string, double> d = new Dictionary<string, double>();
            if (counter == IntPtr.Zero) return d;
            uint size = 0, count = 0;
            try
            {
                uint st = Native.PdhGetFormattedCounterArray(counter, Native.PDH_FMT_DOUBLE, ref size, out count, IntPtr.Zero);
                if (size == 0) return d;
                IntPtr buf = Marshal_Alloc(size);
                if (buf == IntPtr.Zero) return d;
                try
                {
                    st = Native.PdhGetFormattedCounterArray(counter, Native.PDH_FMT_DOUBLE, ref size, out count, buf);
                    if (st != 0 && count == 0) return d;
                    int stride = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.PDH_FMT_COUNTERVALUE_ITEM));
                    for (uint i = 0; i < count; i++)
                    {
                        IntPtr p = (IntPtr)((long)buf + (long)i * stride);
                        Native.PDH_FMT_COUNTERVALUE_ITEM it =
                            (Native.PDH_FMT_COUNTERVALUE_ITEM)System.Runtime.InteropServices.Marshal.PtrToStructure(p, typeof(Native.PDH_FMT_COUNTERVALUE_ITEM));
                        string name = it.szName == IntPtr.Zero ? "" : (System.Runtime.InteropServices.Marshal.PtrToStringUni(it.szName) ?? "");
                        if (it.FmtValue.CStatus == 0)
                        {
                            double v = it.FmtValue.doubleValue;
                            if (!double.IsNaN(v) && !double.IsInfinity(v))
                            {
                                if (d.ContainsKey(name)) d[name] = v; else d.Add(name, v);
                            }
                        }
                    }
                }
                finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
            }
            catch { }
            return d;
        }

        static IntPtr Marshal_Alloc(uint size)
        {
            try { return System.Runtime.InteropServices.Marshal.AllocHGlobal((int)size); }
            catch { return IntPtr.Zero; }
        }

        public void Dispose()
        {
            try { if (_query != IntPtr.Zero) Native.PdhCloseQuery(_query); }
            catch { }
            _query = IntPtr.Zero;
            _opened = false;
        }
    }
}
