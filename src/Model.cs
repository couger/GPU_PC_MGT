using System;
using System.Collections.Generic;

namespace Gpm
{
    public class Volume
    {
        public string Drive = "";
        public double SizeBytes;
        public double FreeBytes;
    }

    /// <summary>一次采样得到的全部指标（发布后不再修改，线程间安全传递）</summary>
    public class Snapshot
    {
        public string Name = "";
        public string Channel = "";
        public bool Ok;
        public string Error = "";
        public DateTime At = DateTime.Now;
        public int CollectMs;

        // 静态信息
        public string Host = "";
        public string Os = "";
        public string CpuName = "";
        public string GpuName = "";
        public string Uptime = "";
        public double RamTotalBytes;
        public List<Volume> Volumes = new List<Volume>();

        // 动态指标
        public double CpuPct;
        public double CpuMhz;
        public List<double> Cores = new List<double>();
        public double MemPct, MemUsedBytes, MemTotalBytes, MemAvailBytes, MemCommittedBytes;
        public double DiskActive, DiskReadBps, DiskWriteBps, DiskQueue;
        public double NetRxBps, NetTxBps;
        public string NetAdapter = "";
        public bool HasGpu;
        public double GpuPct, VramUsed, VramTotal, VramShared;
        public Dictionary<string, double> Engines = new Dictionary<string, double>();
        public double GpuTempC = double.NaN, GpuFanRpm = double.NaN, GpuPowerPct = double.NaN;
        public double SysTempC = double.NaN;
    }

    /// <summary>一个监视目标：名称、通道、最新快照</summary>
    public class Target
    {
        public string Name = "";
        public string Kind = "local";            // local | ssh
        public string Host = "";
        public string User = "";
        public string KeyFile = "";
        public string KnownHosts = "";
        public int Port = 22;
        /// <summary>常驻探针端点的令牌（kind = probe 时使用）</summary>
        public string Token = "";
        public string TlsFingerprint = "";
        public bool AllowInsecureProbe;
        public string Password = "";             // 可选：没有密钥时走密码（需要 askpass 辅助程序）
        public string AskPass = "";
        public int IntervalSec = 2;
        /// <summary>是否采集/显示（本机默认关闭，可在界面上勾选）</summary>
        public volatile bool Enabled = true;
        /// <summary>空闲时的采集间隔（0 = 不启用自适应）</summary>
        public int IdleIntervalSec = 0;
        /// <summary>远端探针存放目录（留空 = C:\ProgramData\GPUPCMGT；路径不能含空格）</summary>
        public string RemoteDir = "";

        public volatile Snapshot Latest;
        public DateTime LastAppended = DateTime.MinValue;

        // UI 侧的滚动历史
        public readonly List<double> HCpu = new List<double>();
        public readonly List<double> HMem = new List<double>();
        public readonly List<double> HDiskR = new List<double>();
        public readonly List<double> HDiskW = new List<double>();
        public readonly List<double> HDiskA = new List<double>();
        public readonly List<double> HNetR = new List<double>();
        public readonly List<double> HNetT = new List<double>();
        public readonly List<double> HGpu = new List<double>();
        public readonly List<double> HTemp = new List<double>();
        public List<List<double>> HCores = new List<List<double>>();
        public const int MaxPoints = 120;
    }
}
