using System;
using System.IO;
using System.Net;
using Gpm;
class Test
{
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);}
    static void Main(string[] args)
    {
        var c=new ProbeConfig{Allowed="192.0.2.5,198.51.100.0/24,2001:db8::/32"};c.SetToken(ProbeConfig.NewToken());c.Validate();
        Check(c.Token.Length==43,"random credential length");
        Check(c.Allows(IPAddress.Parse("192.0.2.5"))&&!c.Allows(IPAddress.Parse("192.0.2.6")),"exact client restriction");
        Check(c.Allows(IPAddress.Parse("198.51.100.4"))&&!c.Allows(IPAddress.Parse("198.51.101.1")),"IPv4 CIDR");
        Check(c.Allows(IPAddress.Parse("2001:db8:1::1"))&&!c.Allows(IPAddress.Parse("2001:db9::1")),"IPv6 CIDR");
        c.Allowed="";Check(c.Allows(IPAddress.Loopback)&&!c.Allows(IPAddress.Parse("192.0.2.5")),"empty permits local only");
        c.Allowed="*";Check(c.Allows(IPAddress.Parse("192.0.2.5")),"explicit unrestricted mode");
        c.Allowed="invalid";bool rejected=false;try{c.Validate();}catch(ArgumentException){rejected=true;}Check(rejected,"invalid rule rejected");c.Allowed="127.0.0.1";
        var p=ConnectionProfile.Parse(c.Profile());Check(p.Token==c.Token&&p.Port==c.Port&&p.Host==c.Host,"copy paste profile roundtrip");
        string path=Path.Combine(args[0],"test-probe-config.json");c.Save(path);string old=c.Token;c.SetToken(ProbeConfig.NewToken());c.Save(path);Check(ProbeConfig.Load(path).Token!=old,"atomic credential rotation");Check(!File.ReadAllText(path).Contains(c.Token),"plaintext credential absent on disk");
    }
}
