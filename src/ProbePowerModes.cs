using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Gpm
{
    public enum ProbePowerMode { Unknown=-1,BestEfficiency=0,Balanced=1,BestPerformance=2 }
    public interface IProbePowerModes
    {
        ProbePowerMode Get(bool pluggedIn);
        void Set(bool pluggedIn,ProbePowerMode mode);
    }
    // Windows user-configured modes, separately stored for AC and battery.
    // These APIs do not change power plans, sleep timers, or the energy saver toggle.
    public sealed class WindowsProbePowerModes : IProbePowerModes
    {
        [DllImport("powrprof.dll")]static extern uint PowerGetUserConfiguredACPowerMode(out Guid mode);
        [DllImport("powrprof.dll")]static extern uint PowerGetUserConfiguredDCPowerMode(out Guid mode);
        [DllImport("powrprof.dll")]static extern uint PowerSetUserConfiguredACPowerMode(ref Guid mode);
        [DllImport("powrprof.dll")]static extern uint PowerSetUserConfiguredDCPowerMode(ref Guid mode);
        static readonly Guid[] Guids={new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a"),Guid.Empty,new Guid("ded574b5-45a0-4f42-8737-46345c09c238")};
        public static readonly string[] Names={"最佳能效","平衡","最佳性能"};
        public static bool PluggedIn {
            get {var source=SystemInformation.PowerStatus.PowerLineStatus;if(source==PowerLineStatus.Unknown)throw new InvalidOperationException("无法确定当前供电方式，请在探针设置中选择插电或电池选项。");return source==PowerLineStatus.Online;}
        }
        public ProbePowerMode Get(bool pluggedIn)
        {
            try {Guid value;uint result=pluggedIn?PowerGetUserConfiguredACPowerMode(out value):PowerGetUserConfiguredDCPowerMode(out value);if(result!=0)throw new Win32Exception((int)result);for(int i=0;i<Guids.Length;i++)if(value==Guids[i])return (ProbePowerMode)i;return ProbePowerMode.Unknown;}
            catch(EntryPointNotFoundException){throw new NotSupportedException("此 Windows 版本不支持电源模式切换，请通过系统电源设置修改。");}
        }
        public void Set(bool pluggedIn,ProbePowerMode mode)
        {
            int index=(int)mode;if(index<0||index>=Guids.Length)throw new ArgumentOutOfRangeException("mode");
            try {Guid value=Guids[index];uint result=pluggedIn?PowerSetUserConfiguredACPowerMode(ref value):PowerSetUserConfiguredDCPowerMode(ref value);if(result!=0)throw new Win32Exception((int)result);if(Get(pluggedIn)!=mode)throw new InvalidOperationException("Windows 未保存所选电源模式，请检查系统策略。");}
            catch(EntryPointNotFoundException){throw new NotSupportedException("此 Windows 版本不支持电源模式切换，请通过系统电源设置修改。");}
        }
        public static string Name(ProbePowerMode mode){int i=(int)mode;return i>=0&&i<Names.Length?Names[i]:"状态未知";}
    }
}
