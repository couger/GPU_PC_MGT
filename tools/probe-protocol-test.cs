using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Gpm;
class ProtocolTest
{
    static string Read(string host,int port,string token)
    {
        using(var client=new TcpClient()){client.ReceiveTimeout=2000;client.Connect(host,port);using(var stream=client.GetStream()){var b=Encoding.ASCII.GetBytes(token+"\n");stream.Write(b,0,b.Length);using(var reader=new StreamReader(stream))return reader.ReadLine();}}
    }
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);}
    static void Main(string[] args)
    {
        int port;var reserve=new TcpListener(IPAddress.Loopback,0);reserve.Start();port=((IPEndPoint)reserve.LocalEndpoint).Port;reserve.Stop();
        string path=Path.Combine(args[0],".tmp","protocol-config.json");var cfg=new ProbeConfig{Port=port,Allowed="",Bind="0.0.0.0",AllowInsecureLan=true};cfg.SetToken(ProbeConfig.NewToken());cfg.Save(path);string original=cfg.Token;
        var start=new ProcessStartInfo(Path.Combine(args[0],"gpm-remote.exe"),"--serve-config \""+path+"\""){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.Combine(args[0],".tmp")};
        using(var process=Process.Start(start))try
        {
            Thread.Sleep(3500);Check(Read("127.0.0.1",port,"wrong").Contains("bad token"),"invalid credential rejected");
            Check(!Read("127.0.0.1",port,original).Contains("bad token"),"valid credential accepted");
            string ip=null;foreach(var candidate in Dns.GetHostAddresses(Dns.GetHostName()))if(candidate.AddressFamily==AddressFamily.InterNetwork&&!IPAddress.IsLoopback(candidate)){ip=candidate.ToString();break;}
            if(ip==null)throw new Exception("No LAN interface for rejection test");
            bool denied=false;try{denied=Read(ip,port,original)==null;}catch(IOException){denied=true;}Check(denied,"real TCP client outside allowlist rejected");
            cfg.Allowed=ip;cfg.Save(path);Thread.Sleep(1200);Check(!Read(ip,port,original).Contains("bad token"),"allowlist hot reload without restart");
            cfg.SetToken(ProbeConfig.NewToken());cfg.Save(path);Thread.Sleep(1200);Check(Read("127.0.0.1",port,original).Contains("bad token"),"old credential rejected after rotation");Check(!Read("127.0.0.1",port,cfg.Token).Contains("bad token"),"new credential accepted");
            reserve=new TcpListener(IPAddress.Loopback,0);reserve.Start();int next=((IPEndPoint)reserve.LocalEndpoint).Port;reserve.Stop();cfg.Port=next;cfg.Save(path);Thread.Sleep(1200);Check(!Read("127.0.0.1",next,cfg.Token).Contains("bad token"),"listener port hot reload");
        }
        finally{if(!process.HasExited){process.Kill();process.WaitForExit();}}
    }
}
