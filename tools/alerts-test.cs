using System;
using System.IO;
using Gpm;
class AlertsTest
{
    static DateTime Start=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc);
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);}
    static AlertNotice Read(AlertMonitor monitor,Target target,int second,bool ok,double cpu=0,double mem=0,double temp=40)
    {return monitor.Observe(target,new Snapshot{Ok=ok,At=Start.AddSeconds(second).ToLocalTime(),CpuPct=cpu,MemPct=mem,GpuTempC=temp},Start.AddSeconds(second));}
    static void Main(string[] args)
    {
        var target=new Target{Name="Simulated",Enabled=true};var monitor=new AlertMonitor();
        Check(Read(monitor,target,0,false)==null&&Read(monitor,target,4,false)==null,"short connection failures are filtered");
        Check(Read(monitor,target,6,false)!=null&&Read(monitor,target,20,false)==null,"startup outage notifies once without prior history");
        Read(monitor,target,30,true);Read(monitor,target,61,true);Read(monitor,target,70,false);
        Check(Read(monitor,target,76,false)!=null,"stable recovery rearms connection notice");
        monitor=new AlertMonitor();Read(monitor,target,0,true,0,0,90);
        Check(Read(monitor,target,6,true,0,0,90)!=null,"high temperature notifies once");
        Check(Read(monitor,target,10,true,0,0,84)==null&&Read(monitor,target,16,true,0,0,90)==null,"temperature hysteresis suppresses threshold oscillation");
        Read(monitor,target,20,true,0,0,75);Read(monitor,target,51,true,0,0,75);Read(monitor,target,60,true,0,0,90);
        Check(Read(monitor,target,66,true,0,0,90)!=null,"stable cooldown rearms temperature notice");
        monitor=new AlertMonitor();Read(monitor,target,0,true,99);Read(monitor,target,15,true,99);Read(monitor,target,20,true,20);Read(monitor,target,60,true,99,95);
        Check(Read(monitor,target,80,true,99,95)==null,"brief resource spikes do not notify");
        var notice=Read(monitor,target,91,true,99,95);Check(notice!=null&&notice.Message.Contains("CPU")&&notice.Message.Contains("内存"),"sustained simultaneous conditions are combined");
        Check(Read(monitor,target,100,true,99,95)==null,"persistent resource saturation is deduplicated");
        monitor=new AlertMonitor();Read(monitor,target,0,true,99);Check(Read(monitor,target,100,true,99)==null,"missing sampling time is not counted as sustained load");
        monitor=new AlertMonitor();var same=new Snapshot{Ok=false};monitor.Observe(target,same,Start);Check(monitor.Observe(target,same,Start.AddMinutes(1))==null,"one stale snapshot cannot trigger notifications");
        target.Enabled=false;Check(Read(monitor,target,120,false)==null,"disabled target does not notify");target.Enabled=true;
        monitor=new AlertMonitor();Read(monitor,target,0,true,0,0,90);Read(monitor,target,3,true,0,0,Double.NaN);Check(Read(monitor,target,6,true,0,0,90)==null,"missing temperature interrupts confirmation");
        string dir=Path.Combine(args[0],".tmp","artifact-test");Directory.CreateDirectory(dir);string log=Path.Combine(dir,"runtime.log");
        File.WriteAllText(log,new string('x',20000));for(int i=0;i<40;i++)LocalFiles.Append(log,new string('a',600)+"\n",4096);
        Check(new FileInfo(log).Length<=4096&&new FileInfo(log+".1").Length<=4096,"current and rotated log stay bounded, including oversized legacy log");
        string stale=Path.Combine(dir,"ssh-error.txt"),config=Path.Combine(dir,"config.json"),export=Path.Combine(dir,"user-report.txt");File.WriteAllText(stale,"old diagnostic");File.WriteAllText(config,"preserve config");File.WriteAllText(export,"preserve export");
        foreach(string path in new[]{stale,config,export})File.SetLastWriteTimeUtc(path,Start.AddDays(-10));LocalFiles.Cleanup(dir,Start);
        Check(!File.Exists(stale)&&File.Exists(config)&&File.Exists(export),"retention deletes only owned diagnostics, preserving configuration and exports");
        LocalFiles.WriteDiagnostic(stale,new string('x',100000));Check(new FileInfo(stale).Length<32768,"error diagnostics are size bounded");
        Config.Dir=args[0];Config.DataDir=dir;
        var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();int port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        var sampler=new RemoteSampler(new Target{Name="Simulated unreachable endpoint",Kind="probe",Host="127.0.0.1",Port=port});
        Check(!sampler.Fetch(true).Ok,"real refused probe connection reports failure");
        var written=File.GetLastWriteTimeUtc(stale);System.Threading.Thread.Sleep(100);sampler.Fetch(true);
        Check(File.GetLastWriteTimeUtc(stale)==written,"repeated identical failure does not rewrite error file");
        listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var server=new System.Threading.Thread(delegate(){using(var client=listener.AcceptTcpClient())using(var stream=client.GetStream()){new StreamReader(stream).ReadLine();byte[] first=System.Text.Encoding.UTF8.GetBytes("{\"host\":\"simulated\",\"cpuPct\":42,");stream.Write(first,0,first.Length);System.Threading.Thread.Sleep(100);byte[] rest=System.Text.Encoding.UTF8.GetBytes("\"gpuTemp\":86}\n");stream.Write(rest,0,rest.Length);}listener.Stop();});server.IsBackground=true;server.Start();
        sampler=new RemoteSampler(new Target{Name="Simulated fragmented endpoint",Kind="probe",Host="127.0.0.1",Port=port,Token="test-only-token"});var result=sampler.Fetch(true);
        Check(result.Ok&&result.CpuPct==42&&result.GpuTempC==86,"fragmented TCP response is assembled before parsing");
    }
}
