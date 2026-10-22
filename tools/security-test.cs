using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Gpm;
class SecurityTest
{
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);}
    static void Reject(Action action,string name){bool failed=false;try{action();}catch(ArgumentException){failed=true;}Check(failed,name);}
    static Snapshot Fetch(string body,string root)
    {
        Config.Dir=root;Config.DataDir=Path.Combine(root,".tmp");
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
        var worker=new Thread(delegate(){try{using(var c=listener.AcceptTcpClient())using(var ns=c.GetStream()){
            ns.ReadTimeout=2000;ProbeWire.ReadLine(ns,128,2000);var bytes=Encoding.UTF8.GetBytes(body+"\n");ns.Write(bytes,0,bytes.Length);
        }}finally{listener.Stop();}});worker.IsBackground=true;worker.Start();
        var result=new RemoteSampler(new Target{Kind="probe",Host="127.0.0.1",Port=port,Token="synthetic-test-token"}).Fetch(true);worker.Join(3000);return result;
    }
    static int Main(string[] args)
    {
        try{
            foreach(string host in new[]{"192.0.2.1 -oProxyCommand=evil","-oProxyCommand=evil","host\nother","host;cmd","user@host","host\"x"})Reject(delegate{SafeInput.Host(host);},"reject unsafe host");
            foreach(string host in new[]{"192.0.2.10","2001:db8::1","computer-name","host.example.test","DESKTOP_1"})SafeInput.Host(host);
            Check(true,"valid DNS IPv4 IPv6 and machine names accepted");
            Reject(delegate{SafeInput.Ssh(new Target{Host="host",User="user -oProxyCommand=evil"});},"reject unsafe SSH user");
            Reject(delegate{SafeInput.RemoteDirectory(@"C:\temp');cmd;('");},"reject remote script injection");
            Check(SafeInput.RemoteDirectory(@"C:\Program Files\Probe").Contains(" "),"remote path supports safely encoded spaces");
            string options=SafeInput.SshOptions(new Target{Host="host",User=@"DOMAIN\user",KeyFile=@"C:\keys with spaces\id",Port=22});
            Check(options.Contains("StrictHostKeyChecking=yes")&&options.Contains(" -- "),"SSH requires enrolled host key and option terminator");
            string root=Path.GetFullPath(args[0]),dir=Path.Combine(root,".tmp","security-tests");Directory.CreateDirectory(dir);Config.Dir=root;Config.DataDir=dir;
            string file=Path.Combine(dir,"viewer-config.json"),secret=ProbeConfig.NewToken();
            File.WriteAllText(file,"{\"targets\":[{\"kind\":\"probe\",\"name\":\"synthetic\",\"host\":\"127.0.0.1\",\"port\":9187,\"token\":\""+secret+"\"}]}");
            var targets=Config.Load(file);Check(targets.Find(delegate(Target t){return t.Kind=="probe";}).Token==secret,"legacy token migrates without losing value");
            Check(!File.ReadAllText(file).Contains(secret)&&File.ReadAllText(file).Contains("tokenProtected"),"legacy token removed from disk and encrypted");
            var acl=File.GetAccessControl(file);bool world=false;foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))if(rule.IdentityReference.Value=="S-1-1-0"&&rule.AccessControlType==AccessControlType.Allow)world=true;
            Check(acl.AreAccessRulesProtected&&!world,"configuration has restricted noninherited ACL");
            var probe=new ProbeConfig{Bind="0.0.0.0"};probe.SetToken(secret);Reject(probe.Validate,"network probe refuses unencrypted default");
            Reject(delegate{ProbeWire.ValidatePin("bad-pin");},"reject invalid certificate fingerprint");
            string valid="{\"protocolVersion\":3,\"ok\":true,\"sampleAgeMs\":2000,\"maxAgeMs\":15000,\"cpuPct\":42}";
            var sample=Fetch(valid,root);Check(sample.Ok&&DateTime.Now-sample.At>TimeSpan.FromSeconds(1),"fresh sample preserves source age rather than receipt time");
            Check(!Fetch(valid.Replace("true","false"),root).Ok,"failed source sample never marked successful");
            Check(!Fetch(valid.Replace("2000","20000"),root).Ok,"stale source sample rejected");
            Check(!Fetch(valid.Replace("2000","-1"),root).Ok,"negative sample age rejected");
            Check(!Fetch("{\"cpuPct\":42}",root).Ok,"old protocol without freshness fails closed");
            Check(!Fetch(new string('x',65537),root).Ok,"oversized response rejected");
            var repeatServer=new TcpListener(IPAddress.Loopback,0);repeatServer.Start();int repeatPort=((IPEndPoint)repeatServer.LocalEndpoint).Port;
            var repeatWorker=new Thread(delegate(){try{for(int i=0;i<2;i++)using(var c=repeatServer.AcceptTcpClient())using(var ns=c.GetStream()){
                ProbeWire.ReadLine(ns,128,2000);byte[] bytes=Encoding.UTF8.GetBytes(valid.Replace("{","{\"sampleId\":\"same-source-packet\",")+"\n");ns.Write(bytes,0,bytes.Length);
            }}finally{repeatServer.Stop();}});repeatWorker.IsBackground=true;repeatWorker.Start();
            var repeated=new RemoteSampler(new Target{Kind="probe",Host="127.0.0.1",Port=repeatPort,Token="synthetic-test-token"});
            var first=repeated.Fetch(true);Thread.Sleep(100);var second=repeated.Fetch(true);repeatWorker.Join(3000);
            Check(first.Ok&&second.Ok&&first.At==second.At,"same source packet never advances sample time on repeated fetch");
            return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
