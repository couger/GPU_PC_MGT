// SSH_ASKPASS helper for GPUPCMonitor.exe
// ssh spawns this program when it needs a password; we print the value of GPM_SSH_PW.
// Build: csc /platform:x64 /out:ssh-askpass.exe src\askpass.cs
class AskPass
{
    static int Main(string[] args)
    {
        string pw = System.Environment.GetEnvironmentVariable("GPM_SSH_PW");
        if (pw == null) pw = "";
        System.Console.WriteLine(pw);
        return 0;
    }
}
