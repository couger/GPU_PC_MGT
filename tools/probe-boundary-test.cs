using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using Gpm;
class ProbeBoundaryTest
{
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);}
    static string Read(int port,string token,string pin){using(var c=new TcpClient()){c.Connect("127.0.0.1",port);c.ReceiveTimeout=2000;c.SendTimeout=2000;using(var stream=ProbeWire.Client(c.GetStream(),"localhost",pin)){byte[] bytes=Encoding.ASCII.GetBytes(token+"\n");stream.Write(bytes,0,bytes.Length);return ProbeWire.ReadLine(stream,65536,2000);}}}
    static int Main(string[] args)
    {
        try{
            string root=args[0];int port;var reserve=new TcpListener(IPAddress.Loopback,0);reserve.Start();port=((IPEndPoint)reserve.LocalEndpoint).Port;reserve.Stop();
            string path=Path.Combine(root,".tmp","boundary-config.json");var cfg=new ProbeConfig{Port=port};cfg.SetToken(ProbeConfig.NewToken());cfg.Save(path);
            string pin="";if(args.Length>1){cfg.CertificateThumbprint=args[1];cfg.Save(path);using(var cert=ProbeWire.ServerCertificate(args[1]))pin=ProbeWire.Fingerprint(cert);}
            var info=new ProcessStartInfo(Path.Combine(root,"gpm-remote.exe"),"--serve-config "+SafeInput.Arg(path)){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.Combine(root,".tmp")};
            using(var process=Process.Start(info))try{
                Thread.Sleep(2500);
                if(args.Length>2&&args[2]=="--expired"){
                    bool rejected=false;try{Read(port,cfg.Token,pin);}catch(System.Security.Authentication.AuthenticationException){rejected=true;}
                    Check(rejected,"expired pinned certificate rejected before authentication");return 0;
                }
                Check(Read(port,cfg.Token,pin).Contains("protocolVersion"),"probe serves protocol with freshness envelope");
                using(var slow=new TcpClient()){
                    slow.Connect("127.0.0.1",port);slow.SendTimeout=1000;
                    Stream dripStream=ProbeWire.Client(slow.GetStream(),"localhost",pin);
                    int stopped=0;var drip=new Thread(delegate(){while(Interlocked.CompareExchange(ref stopped,0,0)==0){try{dripStream.WriteByte((byte)'x');dripStream.Flush();}catch{return;}Thread.Sleep(200);}});drip.IsBackground=true;drip.Start();
                    try{var timer=Stopwatch.StartNew();string good=Read(port,cfg.Token,pin);Check(good.Contains("protocolVersion")&&timer.ElapsedMilliseconds<1800,"slow unauthenticated peer does not block valid client");Thread.Sleep(3500);bool ended=false;try{dripStream.ReadTimeout=1000;ended=dripStream.ReadByte()<0;}catch(IOException){ended=true;}Check(ended,"absolute authentication deadline closes drip connection");}
                    finally{Interlocked.Exchange(ref stopped,1);slow.Close();drip.Join(2000);}
                }
                if(pin.Length>0){bool rejected=false;try{Read(port,cfg.Token,new string('0',64));}catch(System.Security.Authentication.AuthenticationException){rejected=true;}Check(rejected,"wrong pinned certificate rejected before sending token");
                    bool plaintextRejected=false;try{Read(port,cfg.Token,"");}catch{plaintextRejected=true;}Check(plaintextRejected,"TLS endpoint never falls back to plaintext");}
            }finally{if(!process.HasExited){process.Kill();process.WaitForExit();}}
            Assembly assembly=typeof(ProbeConfig).Assembly;var type=assembly.GetType("Gpm.RemoteAgent");
            var json=type.GetMethod("Json",BindingFlags.Static|BindingFlags.NonPublic);
            var failed=new Snapshot{Ok=false,Error="synthetic secret should never be serialized"};string body=(string)json.Invoke(null,new object[]{failed});
            Check(body.Contains("\"ok\":false")&&body.Contains("sampling failed")&&!body.Contains("synthetic secret"),"failed sample serialized as failure with sanitized error");
            var packetType=type.GetNestedType("Packet",BindingFlags.NonPublic);object packet=Activator.CreateInstance(packetType,BindingFlags.Instance|BindingFlags.Public,null,new object[]{new Snapshot{Ok=true}},null);
            packetType.GetField("Tick").SetValue(packet,Stopwatch.GetTimestamp()-Stopwatch.Frequency*30);
            string stale=(string)type.GetMethod("Response",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{packet,3600});Check(stale.Contains("stale sample"),"idle setting change cannot extend a stalled sample lifetime");
            return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
