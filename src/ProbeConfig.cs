using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;

namespace Gpm
{
    public sealed class ProbeConfig
    {
        public int Port=9187;
        public string Bind="0.0.0.0";
        public int Interval=2, Idle=10;
        public string Allowed="";
        public string TokenProtected="";
        public string Host=Environment.MachineName;
        public static string DefaultPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"GPUPCMGT", "probe-config.json"); } }
        [ScriptIgnore] public string Token { get { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(TokenProtected),null,DataProtectionScope.LocalMachine)); } }
        public void SetToken(string token) { TokenProtected=Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token),null,DataProtectionScope.LocalMachine)); }
        public static string NewToken() { byte[] bytes=new byte[32];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(bytes);return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_'); }
        public static ProbeConfig Load(string path) { var c=new JavaScriptSerializer().Deserialize<ProbeConfig>(File.ReadAllText(path));c.Validate();return c; }
        public static void Initialize(string path)
        {
            if(File.Exists(path))throw new IOException("配置已存在，不会覆盖已有凭据");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            var acl=new FileSecurity();acl.SetAccessRuleProtection(true,false);
            acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User,FileSystemRights.FullControl,AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),FileSystemRights.FullControl,AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null),FileSystemRights.FullControl,AccessControlType.Allow));
            var c=new ProbeConfig();c.SetToken(NewToken());c.Validate();
            using(var stream=new FileStream(path,FileMode.CreateNew,FileSystemRights.Write,FileShare.None,4096,FileOptions.None,acl))
            using(var writer=new StreamWriter(stream,Encoding.UTF8))writer.Write(new JavaScriptSerializer().Serialize(c));
        }
        public void Validate()
        {
            IPAddress ip;
            if(Port<1||Port>65535||!IPAddress.TryParse(Bind,out ip))throw new ArgumentException("端口或监听地址无效");
            if(Interval<1||Interval>3600||Idle<Interval||Idle>3600)throw new ArgumentException("采样间隔需为 1–3600 秒，空闲间隔不能小于忙间隔");
            foreach(string rule in Rules()) { if(rule=="*")continue; ParseRule(rule,out ip); }
            string token=Token;if(token.Length<16||token.Length>128)throw new ArgumentException("凭据长度需为 16–128 字符");
            foreach(char ch in token)if(ch<33||ch>126)throw new ArgumentException("凭据不能含空格或非 ASCII 字符");
            if(String.IsNullOrWhiteSpace(Host)||Host.IndexOfAny(new char[]{'\r','\n',' '})>=0)throw new ArgumentException("连接地址无效");
        }
        string[] Rules() { return (Allowed??"").Split(new char[]{',',';','\r','\n',' '},StringSplitOptions.RemoveEmptyEntries); }
        static int ParseRule(string rule,out IPAddress address)
        {
            string[] parts=rule.Split('/');int prefix;
            if(parts.Length>2||!IPAddress.TryParse(parts[0],out address))throw new ArgumentException("访问范围需填写 IP 或 CIDR 网段");
            int bits=address.GetAddressBytes().Length*8;prefix=bits;
            if(parts.Length==2&&(!Int32.TryParse(parts[1],out prefix)||prefix<0||prefix>bits))throw new ArgumentException("CIDR 网段无效");return prefix;
        }
        public bool Allows(IPAddress address)
        {
            if(IPAddress.IsLoopback(address))return true;
            foreach(string rule in Rules())
            {
                if(rule=="*")return true;
                IPAddress network;int prefix=ParseRule(rule,out network);byte[] a=address.GetAddressBytes(),b=network.GetAddressBytes();if(a.Length!=b.Length)continue;
                bool match=true;for(int i=0;i<prefix;i++)if((a[i/8]&(1<<(7-i%8)))!=(b[i/8]&(1<<(7-i%8)))){match=false;break;}if(match)return true;
            }
            return false;
        }
        public void Save(string path)
        {
            Validate();string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {File.WriteAllText(temp,new JavaScriptSerializer().Serialize(this),Encoding.UTF8);if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}finally{if(File.Exists(temp))File.Delete(temp);}
        }
        public string Profile() { return new JavaScriptSerializer().Serialize(new ConnectionProfile{Version=1,Name=Environment.MachineName,Host=Host,Port=Port,Token=Token}); }
    }
    public sealed class ConnectionProfile
    {
        public int Version;public string Name,Host,Token;public int Port;
        public static ConnectionProfile Parse(string text)
        {
            var p=new JavaScriptSerializer().Deserialize<ConnectionProfile>(text);
            if(p==null||p.Version!=1||String.IsNullOrWhiteSpace(p.Host)||p.Port<1||p.Port>65535||String.IsNullOrEmpty(p.Token)||p.Token.Length<16||p.Token.Length>128)throw new ArgumentException("连接凭据格式无效");
            foreach(char ch in p.Token)if(ch<33||ch>126)throw new ArgumentException("凭据字符无效");return p;
        }
    }
}
