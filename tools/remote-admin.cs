using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Diagnostics;
using Gpm;
class RemoteAdmin
{
    static int Main(string[] args)
    {
        try
        {
            Config.Dir = Path.GetFullPath(args[0]);
            Config.DataDir = Path.Combine(Config.Dir, ".tmp");
            Target target = Config.Load(Path.Combine(Config.Dir, "config.json")).Find(delegate(Target t) { return t.Kind == "ssh"; });
            if (target == null) throw new Exception("No SSH target configured");
            if (args.Length == 4 && args[2] == "--host") target.Host = args[3];
            if (args.Length == 4 && (args[1] == "--upload" || args[1] == "--download"))
            {
                ProcessStartInfo p = new ProcessStartInfo("scp");
                p.UseShellExecute=false;p.CreateNoWindow=true;p.RedirectStandardOutput=true;p.RedirectStandardError=true;
                SafeInput.Ssh(target);SafeInput.PathValue(args[2]);SafeInput.PathValue(args[3]);
                p.Arguments="-o StrictHostKeyChecking=yes -o ConnectTimeout=8 -o NumberOfPasswordPrompts=1 -P "+target.Port;
                if(target.KnownHosts.Length>0)p.Arguments+=" -o "+SafeInput.Arg("UserKnownHostsFile="+target.KnownHosts);
                if(target.KeyFile.Length>0)p.Arguments+=" -i "+SafeInput.Arg(target.KeyFile)+" -o IdentitiesOnly=yes";
                else { p.EnvironmentVariables["SSH_ASKPASS"]=Path.Combine(Config.Dir,"ssh-askpass.exe");p.EnvironmentVariables["SSH_ASKPASS_REQUIRE"]="force";p.EnvironmentVariables["GPM_SSH_PW"]=target.Password; }
                string remote=target.User+"@"+target.Host+":"+(args[1]=="--upload"?args[3]:args[2]);
                p.Arguments+=" -- "+(args[1]=="--upload"?SafeInput.Arg(Path.GetFullPath(args[2]))+" "+SafeInput.Arg(remote):SafeInput.Arg(remote)+" "+SafeInput.Arg(Path.GetFullPath(args[3])));
                using(Process transfer=Process.Start(p))
                {
                    var stdout=transfer.StandardOutput.ReadToEndAsync();var stderr=transfer.StandardError.ReadToEndAsync();
                    if(!transfer.WaitForExit(60000)){transfer.Kill();throw new Exception("Upload timed out");}
                    if(transfer.ExitCode!=0)throw new Exception("Upload failed: "+stderr.Result);
                    Console.WriteLine((args[1]=="--upload"?"Uploaded ":"Downloaded ")+Path.GetFileName(args[2]));return 0;
                }
            }
            Config.DataDir = Path.Combine(Config.Dir, ".tmp");
            RemoteSampler sampler = new RemoteSampler(target);
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            string options = sampler.GetType().GetMethod("CommonOptions", flags).Invoke(sampler, null).ToString();
            string script = File.ReadAllText(args[1]);
            string cmd = options + " powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            Console.Write(sampler.GetType().GetMethod("RunSsh", flags).Invoke(sampler, new object[] { cmd, 90000 }));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.GetBaseException().Message); return 1; }
    }
}
