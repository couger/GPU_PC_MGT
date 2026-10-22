using System;
using System.Runtime.InteropServices;

namespace Gpm
{
    internal static class Native
    {
        // ================= 内存 / 运行时间 =================
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile;
            public ulong ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        public static void GetMemory(out double totalBytes, out double availBytes)
        {
            MEMORYSTATUSEX m = new MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            totalBytes = 0; availBytes = 0;
            try
            {
                if (GlobalMemoryStatusEx(ref m)) { totalBytes = m.ullTotalPhys; availBytes = m.ullAvailPhys; }
            }
            catch { }
        }

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        public static TimeSpan Uptime()
        {
            try { return TimeSpan.FromMilliseconds(GetTickCount64()); }
            catch { return TimeSpan.Zero; }
        }

        // ================= PDH 性能计数器 =================
        public const uint PDH_FMT_DOUBLE = 0x00000200;
        public const uint PDH_MORE_DATA = 0x800007D2;

        [StructLayout(LayoutKind.Sequential)]
        public struct PDH_FMT_COUNTERVALUE
        {
            public uint CStatus;
            public double doubleValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PDH_FMT_COUNTERVALUE_ITEM
        {
            public IntPtr szName;
            public PDH_FMT_COUNTERVALUE FmtValue;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern uint PdhOpenQuery(string dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        public static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufSize, out uint itemCount, IntPtr buffer);

        [DllImport("pdh.dll")]
        public static extern uint PdhCloseQuery(IntPtr query);

        // ================= D3DKMT（GPU 温度 / 风扇 / 功耗，任务管理器同源）=================
        public const int KMTQAITYPE_ADAPTERPERFDATA = 62;

        [StructLayout(LayoutKind.Sequential)]
        public struct D3DKMT_QUERYADAPTERINFO
        {
            public uint hAdapter;
            public int Type;
            public IntPtr pPrivateDriverData;
            public uint PrivateDriverDataSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3DKMT_ADAPTERINFO
        {
            public uint hAdapter;
            public long Luid;
            public uint NumOfSources;
            public int bPrecisePresentRegionsPreferred;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3DKMT_ENUMADAPTERS2
        {
            public uint NumAdapters;
            public IntPtr pAdapters;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3DKMT_CLOSEADAPTER
        {
            public uint hAdapter;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3DKMT_ADAPTER_PERFDATA
        {
            public uint PhysicalAdapterIndex;
            public ulong MemoryFrequency, MaxMemoryFrequency, MaxMemoryFrequencyOC, MemoryBandwidth, PCIEBandwidth;
            public uint FanRPM, Power, Temperature;
            public byte PowerStateOverride;
        }

        [DllImport("gdi32.dll")]
        public static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 pEnum);

        [DllImport("gdi32.dll")]
        public static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO pInfo);

        [DllImport("gdi32.dll")]
        public static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER pClose);

        /// <summary>读取所有适配器的 PERFDATA（温度 0.1℃ / 风扇 RPM / 功耗 0.1%TDP）</summary>
        public static void ReadGpuPerf(out double tempC, out double fanRpm, out double powerPct)
        {
            tempC = double.NaN; fanRpm = double.NaN; powerPct = double.NaN;
            int cap = 16;
            int itemSize = Marshal.SizeOf(typeof(D3DKMT_ADAPTERINFO));
            int perfSize = Marshal.SizeOf(typeof(D3DKMT_ADAPTER_PERFDATA));
            IntPtr buf = IntPtr.Zero;
            try
            {
                buf = Marshal.AllocHGlobal(itemSize * cap);
                D3DKMT_ENUMADAPTERS2 en = new D3DKMT_ENUMADAPTERS2();
                en.NumAdapters = (uint)cap;
                en.pAdapters = buf;
                if (D3DKMTEnumAdapters2(ref en) != 0) return;
                uint n = en.NumAdapters;
                if (n > (uint)cap) n = (uint)cap;

                double bestTemp = double.NaN, bestFan = double.NaN, bestPower = double.NaN;
                for (uint i = 0; i < n; i++)
                {
                    IntPtr itemPtr = (IntPtr)((long)buf + (long)i * itemSize);
                    D3DKMT_ADAPTERINFO ai = (D3DKMT_ADAPTERINFO)Marshal.PtrToStructure(itemPtr, typeof(D3DKMT_ADAPTERINFO));
                    IntPtr pbuf = IntPtr.Zero;
                    try
                    {
                        pbuf = Marshal.AllocHGlobal(perfSize);
                        for (int k = 0; k < perfSize; k++) Marshal.WriteByte(pbuf, k, 0);
                        D3DKMT_QUERYADAPTERINFO q = new D3DKMT_QUERYADAPTERINFO();
                        q.hAdapter = ai.hAdapter;
                        q.Type = KMTQAITYPE_ADAPTERPERFDATA;
                        q.pPrivateDriverData = pbuf;
                        q.PrivateDriverDataSize = (uint)perfSize;
                        if (D3DKMTQueryAdapterInfo(ref q) == 0)
                        {
                            D3DKMT_ADAPTER_PERFDATA pd = (D3DKMT_ADAPTER_PERFDATA)Marshal.PtrToStructure(pbuf, typeof(D3DKMT_ADAPTER_PERFDATA));
                            double t = pd.Temperature / 10.0;
                            if (t > 0 && t < 130 && (double.IsNaN(bestTemp) || t > bestTemp)) bestTemp = t;
                            if (pd.FanRPM > 0 && (double.IsNaN(bestFan) || pd.FanRPM > bestFan)) bestFan = pd.FanRPM;
                            if (pd.Power > 0 && (double.IsNaN(bestPower) || pd.Power > bestPower)) bestPower = pd.Power / 10.0;
                        }
                    }
                    finally { if (pbuf != IntPtr.Zero) Marshal.FreeHGlobal(pbuf); }
                    D3DKMT_CLOSEADAPTER cl = new D3DKMT_CLOSEADAPTER();
                    cl.hAdapter = ai.hAdapter;
                    D3DKMTCloseAdapter(ref cl);
                }
                tempC = bestTemp; fanRpm = bestFan; powerPct = bestPower;
            }
            catch { }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        }
    }
}
