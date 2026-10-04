// pdh-remote-probe.cs -- validate the "remote PDH counters" channel natively.
//
// Why a separate probe: the PDH API has NO credential parameter. Remote counter access
// uses the caller's security context, so for a workgroup we need a "network-only" logon
// (LOGON32_LOGON_NEW_CREDENTIALS, i.e. what `runas /netonly` does): the local identity
// stays the same, only the network credentials are swapped.
//
// IMPORTANT LESSON (learned the hard way): while impersonating a network-only token you
// must NOT do local I/O such as writing to the console -- it throws / kills the process.
// So this probe buffers all output and prints it only AFTER RevertToSelf().
//
// Build: csc /platform:x64 /out:pdh-remote-probe.exe tools\pdh-remote-probe.cs
// Run  : pdh-remote-probe.exe <host> <user> <password> [domain]
//
// NOTE: keep this file ASCII-only (csc may read a BOM-less file with the ANSI code page).

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

class PdhRemoteProbe
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LogonUser(string user, string domain, string password, int logonType, int logonProvider, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool ImpersonateLoggedOnUser(IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool RevertToSelf();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhOpenQuery(string dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint fmt, IntPtr type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint fmt, ref uint bufSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    static extern uint PdhCloseQuery(IntPtr query);

    [StructLayout(LayoutKind.Sequential)]
    struct PDH_FMT_COUNTERVALUE { public uint CStatus; public double doubleValue; }

    const uint PDH_FMT_DOUBLE = 0x00000200;
    const int LOGON32_LOGON_NEW_CREDENTIALS = 9;
    const int LOGON32_PROVIDER_WINNT50 = 3;

    static readonly List<string> L = new List<string>();
    static void P(string s) { L.Add(s); }

    static string Explain(uint st)
    {
        string name = "";
        if (st == 0x00000000) name = "PDH_CSTATUS_VALID_DATA (ok)";
        else if (st == 0x800007D1) name = "PDH_CSTATUS_NO_MACHINE";
        else if (st == 0x800007D2) name = "PDH_MORE_DATA (ok, expected here)";
        else if (st == 0x800007D3) name = "PDH_CSTATUS_NO_INSTANCE";
        string win = "";
        try
        {
            string m = new Win32Exception(unchecked((int)st)).Message;
            if (m != null && !m.StartsWith("Unknown error")) win = m;
        }
        catch { }
        string s = "0x" + st.ToString("x8");
        if (name.Length > 0) s += "  " + name;
        if (win.Length > 0) s += "  [" + win + "]";
        return s;
    }

    static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("usage: pdh-remote-probe.exe <host> <user> <password> [domain]");
            return 2;
        }
        string host = args[0], user = args[1], pass = args[2];
        string domain = args.Length > 3 ? args[3] : ".";
        string src = "\\\\" + host;

        P("== remote host " + host + "   user " + user + "   domain " + domain + " ==");
        P("local identity: " + System.Security.Principal.WindowsIdentity.GetCurrent().Name);

        // ---- A) baseline without network credentials (expected to fail) ----
        P("");
        P("[A] baseline WITHOUT network credentials");
        IntPtr q0;
        uint a1 = PdhOpenQuery(src, IntPtr.Zero, out q0);
        P("    PdhOpenQuery(" + src + ") -> " + Explain(a1));
        if (a1 == 0)
        {
            IntPtr c0;
            uint a2 = PdhAddEnglishCounter(q0, src + "\\Memory\\Available MBytes", IntPtr.Zero, out c0);
            P("    PdhAddEnglishCounter    -> " + Explain(a2));
            PdhCloseQuery(q0);
        }

        // ---- B) network-only logon ----
        P("");
        P("[B] LogonUser(LOGON32_LOGON_NEW_CREDENTIALS)");
        IntPtr token;
        bool ok = LogonUser(user, domain, pass, LOGON32_LOGON_NEW_CREDENTIALS, LOGON32_PROVIDER_WINNT50, out token);
        if (!ok && domain != ".")
        {
            int e1 = Marshal.GetLastWin32Error();
            P("    domain '" + domain + "' failed (Win32 " + e1 + "), retrying with '.'");
            ok = LogonUser(user, ".", pass, LOGON32_LOGON_NEW_CREDENTIALS, LOGON32_PROVIDER_WINNT50, out token);
        }
        if (!ok)
        {
            int e = Marshal.GetLastWin32Error();
            P("    FAILED, Win32 " + e + ": " + new Win32Exception(e).Message);
            foreach (string s in L) Console.WriteLine(s);
            return 1;
        }
        P("    OK  (local identity unchanged; only network credentials swapped)");

        bool impersonating = ImpersonateLoggedOnUser(token);
        if (!impersonating)
        {
            int e = Marshal.GetLastWin32Error();
            P("    ImpersonateLoggedOnUser FAILED: " + new Win32Exception(e).Message);
        }

        int rc = 1;
        if (impersonating)
        {
            // ================= everything below runs with the network token =================
            try
            {
                P("");
                P("[C] remote PDH query under those network credentials");
                IntPtr q;
                uint st = PdhOpenQuery(src, IntPtr.Zero, out q);
                P("    PdhOpenQuery -> " + Explain(st));
                if (st == 0)
                {
                    string[] paths = new string[]
                    {
                        src + "\\Processor Information(_Total)\\% Processor Utility",
                        src + "\\Memory\\Available MBytes",
                        src + "\\PhysicalDisk(_Total)\\Disk Read Bytes/sec",
                        src + "\\Thermal Zone Information(*)\\Temperature"
                    };
                    IntPtr[] counters = new IntPtr[paths.Length];
                    string[] addStatus = new string[paths.Length];
                    for (int i = 0; i < paths.Length; i++)
                    {
                        IntPtr c;
                        uint s = PdhAddEnglishCounter(q, paths[i], IntPtr.Zero, out c);
                        counters[i] = c;
                        addStatus[i] = Explain(s);
                    }
                    System.Threading.Thread.Sleep(1200);
                    string collectSt = Explain(PdhCollectQueryData(q));
                    string[] values = new string[paths.Length];
                    for (int i = 0; i < counters.Length; i++)
                    {
                        if (counters[i] == IntPtr.Zero) { values[i] = "(add failed)"; continue; }
                        PDH_FMT_COUNTERVALUE v;
                        uint s = PdhGetFormattedCounterValue(counters[i], PDH_FMT_DOUBLE, IntPtr.Zero, out v);
                        values[i] = v.doubleValue.ToString("0.###") + "   (" + Explain(s) + ")";
                    }
                    // wildcard instance expansion on the remote host
                    IntPtr cw;
                    string wp = src + "\\Processor Information(*)\\% Processor Utility";
                    uint sw = PdhAddEnglishCounter(q, wp, IntPtr.Zero, out cw);
                    string wildSt = Explain(sw);
                    string wildInfo = "";
                    if (sw == 0)
                    {
                        System.Threading.Thread.Sleep(1200);
                        PdhCollectQueryData(q);
                        uint size = 0, count = 0;
                        uint s6 = PdhGetFormattedCounterArray(cw, PDH_FMT_DOUBLE, ref size, out count, IntPtr.Zero);
                        wildInfo = "instances=" + count + " bufSize=" + size + "  (" + Explain(s6) + ")";
                        if (size > 0)
                        {
                            IntPtr buf = Marshal.AllocHGlobal((int)size);
                            try
                            {
                                uint s7 = PdhGetFormattedCounterArray(cw, PDH_FMT_DOUBLE, ref size, out count, buf);
                                wildInfo += "  |  second call instances=" + count + "  (" + Explain(s7) + ")";
                            }
                            finally { Marshal.FreeHGlobal(buf); }
                        }
                    }
                    PdhCloseQuery(q);

                    // ------- now that the query is closed, record the results -------
                    P("");
                    P("    per-counter add status:");
                    for (int i = 0; i < paths.Length; i++)
                        P("      " + paths[i].Substring(src.Length).PadRight(46) + " -> " + addStatus[i]);
                    P("    PdhCollectQueryData -> " + collectSt);
                    for (int i = 0; i < values.Length; i++)
                        P("      value[" + i + "] = " + values[i]);
                    P("");
                    P("[D] wildcard instances on the remote host");
                    P("    " + wp.Substring(src.Length) + " -> " + wildSt);
                    if (wildInfo.Length > 0) P("    " + wildInfo);
                    rc = 0;
                }
                else
                {
                    P("    => could not open a remote counter query.");
                    P("       Check on the target: firewall group 'Performance Logs and Alerts',");
                    P("       Remote Registry service (counter name resolution), and permissions");
                    P("       (Performance Monitor Users, or LocalAccountTokenFilterPolicy=1 in a workgroup).");
                }
            }
            catch (Exception ex)
            {
                P("    EXCEPTION while impersonating: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                RevertToSelf();
            }
        }

        P("");
        if (rc == 0) P("RESULT: remote PDH channel WORKS");
        else P("RESULT: remote PDH channel did not work yet (see above)");

        foreach (string s in L) Console.WriteLine(s);
        CloseHandle(token);
        return rc;
    }
}
