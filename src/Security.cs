using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace Gpm
{
    public static class SafeInput
    {
        public static void Host(string value)
        {
            if(String.IsNullOrEmpty(value)||value.Length>253||Regex.IsMatch(value,@"[\s\x00-\x1f\x7f\""'`$;&|<>]"))throw new ArgumentException("主机地址格式无效");
            IPAddress ip;if(IPAddress.TryParse(value,out ip))return;
            if(!Regex.IsMatch(value,@"\A[A-Za-z0-9_](?:[A-Za-z0-9_.-]*[A-Za-z0-9_.])?\z"))throw new ArgumentException("主机地址格式无效");
        }
        public static void Ssh(Target t)
        {
            Host(t.Host);
            if(String.IsNullOrEmpty(t.User)||t.User.Length>256||t.User[0]=='-'||!Regex.IsMatch(t.User,@"\A[A-Za-z0-9_.@\\-]+\z"))throw new ArgumentException("SSH 用户名格式无效");
            if(t.Port<1||t.Port>65535)throw new ArgumentException("SSH 端口无效");
            PathValue(t.KeyFile);PathValue(t.KnownHosts);
        }
        public static void PathValue(string value){if(value!=null&&Regex.IsMatch(value,"[\\x00-\\x1f\\x7f\"]"))throw new ArgumentException("文件路径含无效字符");}
        public static string RemoteDirectory(string path)
        {
            if(String.IsNullOrWhiteSpace(path))return @"C:\ProgramData\GPUPCMGT";
            if(!Regex.IsMatch(path,@"\A[A-Za-z]:\\[\p{L}\p{N}_ .\\-]+\z")||path.Contains(".."))throw new ArgumentException("远端目录需为普通 Windows 绝对路径");
            return path.TrimEnd('\\');
        }
        // Windows CommandLineToArgvW quoting; caller must not pass this through cmd.exe.
        public static string Arg(string value)
        {
            var b=new StringBuilder("\"");int slashes=0;
            foreach(char c in value){if(c=='\\'){slashes++;continue;}if(c=='"'){b.Append('\\',slashes*2+1).Append(c);slashes=0;continue;}b.Append('\\',slashes).Append(c);slashes=0;}
            return b.Append('\\',slashes*2).Append('"').ToString();
        }
        public static string SshOptions(Target t)
        {
            Ssh(t);var b=new StringBuilder("-o StrictHostKeyChecking=yes -o ConnectTimeout=8 -o NumberOfPasswordPrompts=1 -o ServerAliveInterval=5");
            if(t.KnownHosts.Length>0)b.Append(" -o ").Append(Arg("UserKnownHostsFile="+t.KnownHosts));
            if(t.KeyFile.Length>0)b.Append(" -i ").Append(Arg(t.KeyFile)).Append(" -o IdentitiesOnly=yes -o BatchMode=yes");
            else b.Append(" -o PreferredAuthentications=password,keyboard-interactive");
            return b.Append(" -p ").Append(t.Port).Append(" -l ").Append(Arg(t.User)).Append(" -- ").Append(Arg(t.Host)).ToString();
        }
    }
    public static class PrivateFile
    {
        public static FileSecurity Acl()
        {
            var acl=new FileSecurity();acl.SetAccessRuleProtection(true,false);
            foreach(var sid in new[]{WindowsIdentity.GetCurrent().User,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})
                acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,AccessControlType.Allow));
            return acl;
        }
        public static void Write(string path,string text)
        {
            string full=Path.GetFullPath(path),temp=full+"."+Guid.NewGuid().ToString("N")+".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            // Restrict both the temporary file and final file. DPAPI LocalMachine needs this ACL.
            try{
                using(var stream=new FileStream(temp,FileMode.CreateNew,FileSystemRights.Write,FileShare.None,4096,FileOptions.None,Acl()))
                using(var writer=new StreamWriter(stream,new UTF8Encoding(false)))writer.Write(text);
                if(File.Exists(full)){File.SetAccessControl(full,Acl());File.Replace(temp,full,null);}else File.Move(temp,full);
                File.SetAccessControl(full,Acl());
            }finally{if(File.Exists(temp))File.Delete(temp);}
        }
    }
    public static class ProbeWire
    {
        public static string Fingerprint(X509Certificate cert){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(cert.GetRawCertData())).Replace("-","");}
        public static void ValidatePin(string pin){if(!String.IsNullOrEmpty(pin)&&!Regex.IsMatch(pin,@"\A[0-9a-fA-F]{64}\z"))throw new ArgumentException("TLS 指纹需为 64 位 SHA-256 十六进制值");}
        public static X509Certificate2 ServerCertificate(string thumbprint)
        {
            foreach(var location in new[]{StoreLocation.LocalMachine,StoreLocation.CurrentUser})using(var store=new X509Store(StoreName.My,location)){
                store.Open(OpenFlags.ReadOnly);var found=store.Certificates.Find(X509FindType.FindByThumbprint,thumbprint,false);
                if(found.Count==1&&found[0].HasPrivateKey)return found[0];
            }
            throw new InvalidOperationException("探针 TLS 证书或私钥不可用");
        }
        public static Stream Client(NetworkStream raw,string host,string pin)
        {
            if(String.IsNullOrEmpty(pin))return raw;
            ValidatePin(pin);
            var tls=new SslStream(raw,false,delegate(object sender,X509Certificate cert,X509Chain chain,SslPolicyErrors errors){
                if(cert==null)return false;var c=new X509Certificate2(cert);
                using(c)return String.Equals(Fingerprint(c),pin,StringComparison.OrdinalIgnoreCase)&&DateTime.Now>=c.NotBefore&&DateTime.Now<=c.NotAfter;
            });
            try{
                using(var deadline=new System.Threading.Timer(delegate{try{raw.Close();}catch{}},null,5000,System.Threading.Timeout.Infinite))
                    tls.AuthenticateAsClient(host,null,SslProtocols.Tls12,false);
                return tls;
            }catch{tls.Dispose();throw;}
        }
        public static Stream Server(NetworkStream raw,X509Certificate2 cert)
        {
            if(cert==null)return raw;var tls=new SslStream(raw,false);
            try{tls.AuthenticateAsServer(cert,false,SslProtocols.Tls12,false);return tls;}catch{tls.Dispose();throw;}
        }
        public static string ReadLine(Stream stream,int max,int timeoutMs)
        {
            var timer=Stopwatch.StartNew();var data=new MemoryStream();
            using(data){while(true){int remaining=timeoutMs-(int)timer.ElapsedMilliseconds;if(remaining<=0)throw new TimeoutException("响应超时");stream.ReadTimeout=remaining;
                int ch=stream.ReadByte();if(ch<0)throw new EndOfStreamException("响应未完整结束");if(ch==10)return Encoding.UTF8.GetString(data.ToArray()).TrimEnd('\r');
                if(data.Length>=max)throw new IOException("响应超过大小限制");data.WriteByte((byte)ch);
            }}
        }
    }
}
