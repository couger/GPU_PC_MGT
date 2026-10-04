using System;
using System.Collections.Generic;

namespace Gpm
{
    public sealed class HistorySample
    {
        public DateTime ReceivedUtc;
        public Snapshot Data;
    }
    public sealed class FaultRecord
    {
        public string Name,Host,Error;
        public DateTime FaultUtc;
        public long Sequence;
        public HistorySample[] Samples;
        public override string ToString(){return FaultUtc.ToLocalTime().ToString("MM-dd HH:mm:ss")+"  "+Name;}
    }
    public sealed class FaultRecorder
    {
        sealed class Buffer{public readonly List<HistorySample> Samples=new List<HistorySample>();public bool LastOk;}
        readonly object _gate=new object();
        readonly Dictionary<Target,Buffer> _buffers=new Dictionary<Target,Buffer>();
        readonly List<FaultRecord> _records=new List<FaultRecord>();
        long _sequence;
        public FaultRecord Observe(Target target,Snapshot sample,DateTime utc)
        {
            lock(_gate)
            {
                Cleanup(utc);Buffer buffer;if(!_buffers.TryGetValue(target,out buffer)){buffer=new Buffer();_buffers.Add(target,buffer);}
                Prune(buffer,utc);buffer.Samples.Add(new HistorySample{ReceivedUtc=utc,Data=sample});
                if(buffer.Samples.Count>3000)buffer.Samples.RemoveAt(0);
                FaultRecord saved=null;
                if(!sample.Ok&&buffer.LastOk&&buffer.Samples.Exists(delegate(HistorySample point){return point.Data.Ok;}))
                {
                    saved=new FaultRecord{Name=target.Name,Host=target.Host,Error=sample.Error,FaultUtc=utc,Sequence=++_sequence,Samples=buffer.Samples.ToArray()};
                    _records.Add(saved);while(_records.Count>10)_records.RemoveAt(0);
                }
                buffer.LastOk=sample.Ok;return saved;
            }
        }
        static void Prune(Buffer buffer,DateTime utc){buffer.Samples.RemoveAll(delegate(HistorySample point){return point.ReceivedUtc<utc.AddMinutes(-5);});}
        void Cleanup(DateTime utc)
        {
            _records.RemoveAll(delegate(FaultRecord record){return utc-record.FaultUtc>=TimeSpan.FromHours(24);});
            foreach(var buffer in _buffers.Values)Prune(buffer,utc);
        }
        public FaultRecord[] Records(DateTime utc){lock(_gate){Cleanup(utc);var result=_records.ToArray();Array.Reverse(result);return result;}}
    }
    public static class FaultHistory
    {
        public static readonly FaultRecorder Recorder=new FaultRecorder();
        public static void Observe(Target target,Snapshot sample)
        {
            try{Recorder.Observe(target,sample,DateTime.UtcNow);}catch(Exception ex){UiLog.W("temporary history: "+ex.GetType().Name);}
        }
        public static FaultRecord[] Records(){return Recorder.Records(DateTime.UtcNow);}
    }
}
