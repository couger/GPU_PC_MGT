// tools/traydiag.cs -- 托盘问题专项诊断（结构正确版）
//
// 修正两处此前的诊断错误：
//   1) Shell_NotifyIconGetRect 要的是 40 字节的 NOTIFYICONIDENTIFIER，不是 976 字节的 NOTIFYICONDATA
//   2) 不要用 Process.GetProcessesByName 判断外壳是否在跑（在该上下文里会假阴性），改用 FindWindow
// 另外打印本进程所在的窗口站/桌面名，判断是否与外壳同桌面。
//
// 编译：csc /platform:x64 /target:winexe /out:traydiag.exe tools\traydiag.cs
// 运行：traydiag.exe   -> 结果写 tray-diag.log，8 秒后自动退出（不发任何破坏性操作）

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

class TrayDiag
{
    const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    const int NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_SHOWTIP = 0x80;
    const int WM_TRAY = 0x8001;
    const int UOI_NAME = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize; public IntPtr hWnd; public int uID; public int uFlags; public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState; public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    // 注意：这个结构只有 40 字节（x64），与上面的 NOTIFYICONDATA 完全不同
    [StructLayout(LayoutKind.Sequential)]
    struct NOTIFYICONIDENTIFIER
    {
        public int cbSize; public IntPtr hWnd; public int uID; public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int L, T, R, B; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool Shell_NotifyIconW(int message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll")]
    static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT r);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string cls, string win);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr h);

    [DllImport("user32.dll")]
    static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll")]
    static extern IntPtr GetThreadDesktop(int threadId);

    [DllImport("kernel32.dll")]
    static extern int GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetUserObjectInformation(IntPtr h, int index, StringBuilder info, int len, out int needed);

    static readonly StringBuilder Log = new StringBuilder();
    static void L(string s) { Log.Append(s).Append("\r\n"); Console.WriteLine(s); }

    static string ObjName(IntPtr h)
    {
        StringBuilder sb = new StringBuilder(256);
        int need;
        if (GetUserObjectInformation(h, UOI_NAME, sb, 512, out need)) return sb.ToString();
        return "(未知, err=" + Marshal.GetLastWin32Error() + ")";
    }

    static void RectOf(Form f, string tag)
    {
        NOTIFYICONIDENTIFIER id = new NOTIFYICONIDENTIFIER();
        id.cbSize = Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER));
        id.hWnd = f.Handle;
        id.uID = 1;
        id.guidItem = Guid.Empty;
        RECT r;
        int hr = Shell_NotifyIconGetRect(ref id, out r);
        string note;
        if (hr == 0) note = "图标在托盘里，位置=(" + r.L + "," + r.T + ")-(" + r.R + "," + r.B + ")";
        else if (hr == unchecked((int)0x80070490)) note = "外壳里没有这个图标 (ERROR_NOT_FOUND)";
        else if (hr == unchecked((int)0x80070057)) note = "标识无效 (E_INVALIDARG)";
        else note = "(hr)";
        L(tag + "  GetRect cbSize=" + id.cbSize + " hr=0x" + hr.ToString("x8") + "  " + note);
    }

    static bool Add(Form f, IntPtr hicon, string tag)
    {
        NOTIFYICONDATA d = new NOTIFYICONDATA();
        d.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
        d.hWnd = f.Handle; d.uID = 1;
        d.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        d.uCallbackMessage = WM_TRAY;
        d.hIcon = hicon;
        d.szTip = "traydiag " + tag; d.szInfo = ""; d.szInfoTitle = "";
        Marshal.GetLastWin32Error();
        bool ok = Shell_NotifyIconW(NIM_ADD, ref d);
        int err = Marshal.GetLastWin32Error();
        L(tag + "  NIM_ADD ok=" + ok + "  win32err=" + err + "  cbSize=" + d.cbSize
          + "  hwnd=" + f.Handle.ToInt64() + "  icon=" + hicon.ToInt64()
          + "  trayVisible=" + IsWindowVisible(FindWindow("Shell_TrayWnd", null)));
        if (ok)
        {
            NOTIFYICONDATA v = d; v.uVersion = 4;
            L("    NIM_SETVERSION(v4) ok=" + Shell_NotifyIconW(NIM_SETVERSION, ref v) + " win32err=" + Marshal.GetLastWin32Error());
        }
        return ok;
    }

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        L("== 环境（修正后的判断方式）==");
        L("  session=" + System.Diagnostics.Process.GetCurrentProcess().SessionId);
        L("  窗口站 WinSta = " + ObjName(GetProcessWindowStation()));
        L("  桌面 Desktop  = " + ObjName(GetThreadDesktop(GetCurrentThreadId())));
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        IntPtr prog = FindWindow("Progman", null);
        L("  Shell_TrayWnd = " + (tray == IntPtr.Zero ? "不存在" : ("存在 hwnd=" + tray.ToInt64() + " 可见=" + IsWindowVisible(tray))));
        L("  Progman       = " + (prog == IntPtr.Zero ? "不存在" : ("存在 hwnd=" + prog.ToInt64())));
        L("  NOTIFYICONDATA 大小 = " + Marshal.SizeOf(typeof(NOTIFYICONDATA)) + "  NOTIFYICONIDENTIFIER 大小 = " + Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER)));

        Form f = new Form();
        f.Text = "traydiag";
        f.ClientSize = new Size(320, 90);
        f.StartPosition = FormStartPosition.CenterScreen;

        L("== 显示前注册 ==");
        Add(f, SystemIcons.Application.Handle, "before-shown");
        RectOf(f, "before-shown");

        f.Shown += delegate
        {
            L("== 显示后注册（共享图标）==");
            Add(f, SystemIcons.Application.Handle, "after-shown-shared");
            RectOf(f, "after-shown-shared");

            L("== 显示后注册（私有图标）==");
            Icon priv = new Icon(SystemIcons.Application, SystemInformation.SmallIconSize);
            Add(f, priv.Handle, "after-shown-private");
            RectOf(f, "after-shown-private");
        };

        Timer t = new Timer();
        t.Interval = 8000;
        t.Tick += delegate
        {
            t.Stop();
            RectOf(f, "8 秒后复核");
            L("== 结束（不做任何修改性操作）==");
            f.Close();
        };
        t.Start();

        Application.Run(f);

        try { System.IO.File.WriteAllText("tray-diag.log", Log.ToString(), new UTF8Encoding(false)); }
        catch { }
    }
}
