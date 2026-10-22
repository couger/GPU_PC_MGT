using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using Gpm;
class SamplerTest
{
    static void Main(string[] args)
    {
        Config.Dir=args[0];Config.DataDir=Path.Combine(args[0],".tmp");var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
        var target=new Target{Name="本地断连模拟",Kind="probe",Host="127.0.0.1",Port=port,Token="test-only-credential-1234",IntervalSec=1};
        var server=new Thread(delegate(){try{for(int i=0;i<3;i++)using(var client=listener.AcceptTcpClient())using(var stream=client.GetStream()){var reader=new StreamReader(stream,Encoding.ASCII);reader.ReadLine();byte[] payload=Encoding.UTF8.GetBytes("{\"protocolVersion\":3,\"ok\":true,\"sampleAgeMs\":0,\"maxAgeMs\":15000,\"host\":\"simulated\",\"cpuPct\":"+(15+i*10)+",\"memTotal\":68719476736,\"memAvail\":42949672960,\"gpuPct\":95,\"gpuTemp\":"+(85+i)+",\"engines\":{\"compute\":95}}\n");stream.Write(payload,0,payload.Length);}}finally{listener.Stop();}});server.IsBackground=true;server.Start();
        typeof(Config).Assembly.GetType("Gpm.Program").GetMethod("StartSamplers",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{new List<Target>{target}});
        DateTime deadline=DateTime.UtcNow.AddSeconds(15);while(FaultHistory.Records().Length==0&&DateTime.UtcNow<deadline)Thread.Sleep(100);target.Enabled=false;
        var records=FaultHistory.Records();if(records.Length!=1||records[0].Samples.Length!=4)throw new Exception("Automatic capture did not receive three valid samples and a failed boundary");
        if(records[0].Samples[2].Data.GpuTempC!=87||records[0].Samples[3].Data.Ok)throw new Exception("Recorded measurements are not authentic to the actual sampler");
        Console.WriteLine("PASS actual TCP sampler automatically freezes history after endpoint becomes unreachable");
        Console.WriteLine("PASS no live remote device was stopped; captured temperatures and failure boundary are correct");
    }
}
