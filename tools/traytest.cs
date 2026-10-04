// tools/traytest.cs -- 最小托盘测试：定位 Shell_NotifyIcon(NIM_ADD) 返回 ERROR_INVALID_WINDOW_HANDLE(1400) 的原因
//
// 在同一个进程里做三次对照：
//   C) 窗口 Show() 之前注册（复现 .NET NotifyIcon 与我之前的时机）
//   A) 窗口 Show() 之后注册，共享图标句柄 SystemIcons.Application
//   B) 窗口 Show() 之后注册，私有图标副本
// 最后用 Shell_NotifyIconGetRect 向外壳复核"图标到底在不在"。
//
// 编译：csc /platform:x64 /target:winexe /out:traytest.exe tools\traytest.cs
// 运行：traytest.exe        结果写在 tray-test.log（本程序 6 秒后自动退出）

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

class TrayTest
{
    const int NIM_ADD = 0, NIM_DELETE = 2, NIM_SETVERSION = 4;
    const int NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_SHOWTIP = 0x80;
    const int WM_TRAY = 0x8001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NID
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool Shell_NotifyIconW(int message, ref NID data);

    [DllImport("shell32.dll")]
    static extern int Shell_NotifyIconGetRect(ref NID id, out RECT r);

    [DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr h);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int L, T, R, B; }

    static readonly StringBuilder Log = new StringBuilder();

    static void L(string s)
    {
        Log.Append(s).Append("\r\n");
        Console.WriteLine(s);
    }

    static NID New(Form f, IntPtr hicon, string tip)
    {
        NID d = new NID();
        d.cbSize = Marshal.SizeOf(typeof(NID));
        d.hWnd = f.Handle;
        d.uID = 1;
        d.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        d.uCallbackMessage = WM_TRAY;
        d.hIcon = hicon;
        d.szTip = tip;
        d.szInfo = "";
        d.szInfoTitle = "";
        return d;
    }

    static void Add(Form f, IntPtr hicon, string tag)
    {
        NID d = New(f, hicon, "traytest " + tag);
        bool ok = Shell_NotifyIconW(NIM_ADD, ref d);
        int err = Marshal.GetLastWin32Error();
        L(tag + "  NIM_ADD ok=" + ok + "  win32err=" + err
          + "  hwnd=" + f.Handle.ToInt64() + "  cbSize=" + d.cbSize + "  icon=" + hicon.ToInt64()
          + "  windowValid=" + IsWindow(f.Handle) + "  visible=" + IsWindowVisible(f.Handle));
        if (!ok) return;
        NID v = New(f, hicon, d.szTip);
        v.uVersion = 4;
        bool ok2 = Shell_NotifyIconW(NIM_SETVERSION, ref v);
        L("    NIM_SETVERSION(v4) ok=" + ok2 + " win32err=" + Marshal.GetLastWin32Error());
    }

    static void Rect(Form f, string tag)
    {
        NID id = new NID();
        id.cbSize = Marshal.SizeOf(typeof(NID));
        id.hWnd = f.Handle;
        id.uID = 1;
        id.guidItem = Guid.Empty;
        RECT r;
        int hr = Shell_NotifyIconGetRect(ref id, out r);
        L(tag + "  Shell_NotifyIconGetRect hr=0x" + hr.ToString("x8")
          + (hr == 0 ? ("  图标位置=(" + r.L + "," + r.T + ")-(" + r.R + "," + r.B + ")") : "  (外壳里没有)"));
    }

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Form f = new Form();
        f.Text = "traytest";
        f.ClientSize = new Size(320, 100);
        f.StartPosition = FormStartPosition.CenterScreen;

        L("== 环境 ==");
        L("  session=" + System.Diagnostics.Process.GetCurrentProcess().SessionId
          + "  explorer运行中=" + (System.Diagnostics.Process.GetProcessesByName("explorer").Length > 0));

        // C) 显示之前注册（.NET NotifyIcon 的时机）
        L("== C) Show() 之前 ==");
        Add(f, SystemIcons.Application.Handle, "C-before-shown");
        Rect(f, "C-before-shown");

        f.Shown += delegate
        {
            L("== 窗口已显示 ==");
            L("  hwnd=" + f.Handle.ToInt64() + " IsWindow=" + IsWindow(f.Handle) + " IsWindowVisible=" + IsWindowVisible(f.Handle));

            // A) 显示之后用共享图标
            L("== A) Show() 之后，共享图标 ==");
            Add(f, SystemIcons.Application.Handle, "A-after-shown-shared");
            Rect(f, "A-after-shown-shared");

            // B) 显示之后用私有图标
            L("== B) Show() 之后，私有图标 ==");
            Icon priv = new Icon(SystemIcons.Application, SystemInformation.SmallIconSize);
            Add(f, priv.Handle, "B-after-shown-private");
            Rect(f, "B-after-shown-private");
        };

        Timer t = new Timer();
        t.Interval = 6000;
        t.Tick += delegate
        {
            t.Stop();
            Rect(f, "6秒后复核");
            L("== 结束 ==");
            f.Close();
        };
        t.Start();

        Application.Run(f);

        try { System.IO.File.WriteAllText("tray-test.log", Log.ToString(), new UTF8Encoding(false)); }
        catch { }
    }
}
