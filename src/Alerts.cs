using System;
using System.Collections.Generic;

namespace Gpm
{
    public sealed class AlertNotice
    {
        public Target Target;
        public string Message;
    }
    // Conditions latch until sustained recovery; repeated readings cannot repeat a notice.
    public sealed class AlertMonitor
    {
        sealed class Condition { public DateTime Since,Recovery;public bool Active; }
        sealed class Device { public Snapshot Last;public readonly Dictionary<string,Condition> Conditions=new Dictionary<string,Condition>(); }
        readonly Dictionary<Target,Device> _devices=new Dictionary<Target,Device>();
        static bool Check(Device device,string key,bool high,bool recovered,DateTime utc,int delay)
        {
            Condition state;if(!device.Conditions.TryGetValue(key,out state)){state=new Condition();device.Conditions[key]=state;}
            if(state.Active)
            {
                if(!recovered){state.Recovery=DateTime.MinValue;return false;}
                if(state.Recovery==DateTime.MinValue)state.Recovery=utc;
                if(utc-state.Recovery>=TimeSpan.FromSeconds(30)){state.Active=false;state.Since=DateTime.MinValue;state.Recovery=DateTime.MinValue;}
                return false;
            }
            if(!high){state.Since=DateTime.MinValue;return false;}
            if(state.Since==DateTime.MinValue)state.Since=utc;
            if(utc-state.Since<TimeSpan.FromSeconds(delay))return false;
            state.Active=true;return true;
        }
        static bool Valid(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
        public AlertNotice Observe(Target target,Snapshot sample,DateTime utc)
        {
            if(!target.Enabled){_devices.Remove(target);return null;}
            if(sample==null)return null;
            Device device;if(!_devices.TryGetValue(target,out device)){device=new Device();_devices.Add(target,device);}
            if(ReferenceEquals(device.Last,sample))return null;
            // Long sampling gaps do not count as sustained load or recovery.
            if(device.Last!=null&&sample.At-device.Last.At>TimeSpan.FromSeconds(Math.Max(60,target.IdleIntervalSec*2)))
                foreach(var condition in device.Conditions.Values){condition.Since=DateTime.MinValue;condition.Recovery=DateTime.MinValue;}
            device.Last=sample;var messages=new List<string>();
            if(Check(device,"connection",!sample.Ok,sample.Ok,utc,5))messages.Add("连接异常，请检查目标地址、探针服务和网络；故障记录可查看已有数据");
            if(!sample.Ok)
            {
                foreach(var item in device.Conditions)if(item.Key!="connection"){item.Value.Since=DateTime.MinValue;item.Value.Recovery=DateTime.MinValue;}
            }
            else
            {
                Metric(device,messages,"GPU 温度",sample.GpuTempC,85,80,5,utc,"°C");
                Metric(device,messages,"系统温度",sample.SysTempC,85,80,5,utc,"°C");
                Metric(device,messages,"CPU 占用",sample.CpuPct,95,85,30,utc,"%");
                Metric(device,messages,"内存占用",sample.MemPct,90,80,30,utc,"%");
                Metric(device,messages,"GPU 占用",sample.HasGpu?sample.GpuPct:Double.NaN,95,85,30,utc,"%");
                Metric(device,messages,"磁盘活动",sample.DiskActive,95,85,30,utc,"%");
            }
            return messages.Count==0?null:new AlertNotice{Target=target,Message=String.Join("；",messages.ToArray())};
        }
        static void Metric(Device device,List<string> messages,string key,double value,double high,double low,int delay,DateTime utc,string unit)
        {
            if(!Valid(value)){Condition state;if(device.Conditions.TryGetValue(key,out state)){state.Since=DateTime.MinValue;state.Recovery=DateTime.MinValue;}return;}
            if(Check(device,key,value>=high,value<=low,utc,delay))messages.Add(key+"较高（"+value.ToString("0")+unit+"）");
        }
    }
}
