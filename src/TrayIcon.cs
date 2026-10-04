using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Gpm
{
    /// <summary>
    /// 原生托盘图标：直接调用 Shell_NotifyIcon。
    ///
    /// 检查注册结果，避免注册失败时隐藏窗口。错误码仅作为诊断线索。
    ///
    /// 另外处理 TaskbarCreated：资源管理器重启会清空所有托盘图标，收到该消息必须重新添加。
    /// </summary>
    class TrayIcon : IDisposable
    {
        const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
        const int NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
        const int NOTIFYICON_VERSION_4 = 4;
        const int WM_TRAY = 0x8001;              // WM_APP + 1
        const int NIIF_INFO = 0x01;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NOTIFYICONDATA
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

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);
        [StructLayout(LayoutKind.Sequential)]struct IconIdentifier { public int Size;public IntPtr Window;public int Id;public Guid Guid; }
        [StructLayout(LayoutKind.Sequential)]struct IconRect { public int Left,Top,Right,Bottom; }
        [DllImport("shell32.dll")]static extern int Shell_NotifyIconGetRect(ref IconIdentifier identifier,out IconRect rect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int RegisterWindowMessage(string message);

        /// <summary>拦截宿主窗口消息（NativeWindow.AssignHandle，不改动 Form 本身）</summary>
        class Hook : NativeWindow
        {
            public TrayIcon Owner;
            protected override void WndProc(ref Message m)
            {
                try { if (Owner != null) Owner.OnMessage(ref m); }
                catch { }
                base.WndProc(ref m);
            }
        }

        readonly Form _form;
        readonly Hook _hook = new Hook();
        readonly int _taskbarCreated;
        Icon _icon;
        readonly Icon _normalIcon;
        readonly Icon _warningIcon;
        readonly TrayFlyout _flyout = new TrayFlyout();
        readonly bool _richTooltip;
        string _tip = "";
        bool _added;
        bool _v4;
        bool _disposed;
        IntPtr _registeredWindow;
        DateTime _lastAttempt = DateTime.MinValue;

        public event EventHandler Clicked;        // 左键单击/双击
        public event EventHandler MenuRequested;  // 右键
        public event EventHandler Unavailable;

        public TrayIcon(Form form, string tip) : this(form, tip, "monitor") { }

        public TrayIcon(Form form, string tip, string iconName)
        {
            _form = form;
            _tip = tip ?? "";
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            _normalIcon = AppIcons.Load(iconName, SystemInformation.SmallIconSize);
            _warningIcon = AppIcons.Load(iconName + "-warning", SystemInformation.SmallIconSize);
            _icon = _normalIcon;
            _richTooltip = true;
            _flyout.SetText(_tip);
            _hook.Owner = this;
            _hook.AssignHandle(form.Handle);
            form.HandleCreated += HandleCreated;
            form.HandleDestroyed += HandleDestroyed;
            Add();
            // 若窗口尚未显示时注册失败，等窗口真正显示后再补一次，并把结果写进日志
            form.Shown += FormShown;
            form.VisibleChanged += FormVisibleChanged;
        }

        void HandleCreated(object sender, EventArgs e) { _hook.AssignHandle(_form.Handle); Add(); }
        void HandleDestroyed(object sender, EventArgs e) { Remove(); _v4 = false; _hook.ReleaseHandle(); }
        void FormShown(object sender, EventArgs e) { EnsureAdded(); }
        void FormVisibleChanged(object sender, EventArgs e) { if (_form.Visible) EnsureAdded(); }

        public bool EnsureAdded()
        {
            if (!_added) Add();
            return _added;
        }

        public bool Added { get { return _added; } }

        NOTIFYICONDATA New()
        {
            NOTIFYICONDATA d = new NOTIFYICONDATA();
            d.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
            d.hWnd = _form.Handle;
            d.uID = 1;
            d.szTip = "";
            d.szInfo = "";
            d.szInfoTitle = "";
            return d;
        }

        public void Add()
        {
            if (_disposed || _added || !_form.IsHandleCreated) return;
            _lastAttempt = DateTime.UtcNow;
            _v4 = false;
            NOTIFYICONDATA d = New();
            d.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            d.uCallbackMessage = WM_TRAY;
            d.hIcon = _icon.Handle;
            d.szTip = Trim(_tip, 127);
            bool ok = Shell_NotifyIcon(NIM_ADD, ref d);
            int err = Marshal.GetLastWin32Error();
            _added = ok;
            if (ok) _registeredWindow = d.hWnd;
            UiLog.W("tray NIM_ADD ok=" + ok + " lastErrorHint=" + err + " (not a guaranteed failure reason) hwnd=" + d.hWnd.ToInt64() + " cbSize=" + d.cbSize + " icon=" + d.hIcon.ToInt64());
            if (!ok) return;
            NOTIFYICONDATA v = New();
            v.uVersion = NOTIFYICON_VERSION_4;
            _v4 = Shell_NotifyIcon(NIM_SETVERSION, ref v);
            UiLog.W("tray NIM_SETVERSION(v4) ok=" + _v4);
            if (!_v4) { d.uFlags = NIF_TIP | NIF_SHOWTIP; Shell_NotifyIcon(NIM_MODIFY, ref d); }
        }

        public void SetWarning(bool warning)
        {
            Icon next = warning ? _warningIcon : _normalIcon;
            if (_disposed || _icon == next) return;
            _icon = next;
            if (!_added) return;
            NOTIFYICONDATA d=New();d.uFlags=NIF_ICON;d.hIcon=_icon.Handle;
            Shell_NotifyIcon(NIM_MODIFY,ref d);
        }

        public void HideFlyout() { _flyout.Hide(); }

        /// <summary>更新悬停提示（只在文本变化时通知外壳）</summary>
        public void Update(string tip)
        {
            if (_disposed) return;
            if (!_added && (DateTime.UtcNow - _lastAttempt).TotalSeconds >= 5) Add();
            string t = tip ?? "";
            if (t == _tip) return;
            _tip = t;
            _flyout.SetText(t);
            if (!_added) return;
            NOTIFYICONDATA d = New();
            d.uFlags = NIF_TIP | (_v4 && _richTooltip ? 0 : NIF_SHOWTIP);
            d.szTip = Trim(t,127);
            if (!Shell_NotifyIcon(NIM_MODIFY, ref d))
            {
                UiLog.W("tray NIM_MODIFY(tip) failed lastErrorHint=" + Marshal.GetLastWin32Error());
                _added = false;
                if (Unavailable != null) Unavailable(this, EventArgs.Empty);
            }
        }

        public void Balloon(string title, string text)
        {
            if (!_added) return;
            NOTIFYICONDATA d = New();
            d.uFlags = NIF_INFO;
            d.szTip = _tip;
            d.szInfoTitle = Trim(title, 63);
            d.szInfo = Trim(text, 255);
            d.dwInfoFlags = NIIF_INFO;
            if (!Shell_NotifyIcon(NIM_MODIFY, ref d))
                UiLog.W("tray NIM_MODIFY(info) failed lastErrorHint=" + Marshal.GetLastWin32Error());
        }

        public void OnMessage(ref Message m)
        {
            if (_disposed) return;
            if (_taskbarCreated != 0 && m.Msg == _taskbarCreated)
            {
                UiLog.W("explorer restarted (TaskbarCreated) -> re-adding tray icon");
                _added = false;
                Add();
                if (!_added && Unavailable != null) Unavailable(this, EventArgs.Empty);
                return;
            }
            if (m.Msg != WM_TRAY) return;

            long lp = m.LParam.ToInt64();
            int evt;
            if (_v4) evt = (int)(lp & 0xFFFF);       // v4: lParam 低 16 位是事件，wParam 是坐标
            else evt = (int)lp;                      // 旧版：wParam 是图标 ID，lParam 是事件

            if (evt == 0x0406 && _richTooltip)
            {
                // Query the actual icon. Callback coordinates can be zero or use a
                // different DPI coordinate space; they must not anchor a card at (0,0).
                IconIdentifier id=new IconIdentifier{Size=Marshal.SizeOf(typeof(IconIdentifier)),Window=_registeredWindow,Id=1};IconRect rect;
                if(Shell_NotifyIconGetRect(ref id,out rect)==0&&rect.Right>rect.Left&&rect.Bottom>rect.Top)
                    _flyout.ShowAtIcon(_flyout.ToLogical(Rectangle.FromLTRB(rect.Left,rect.Top,rect.Right,rect.Bottom)));
                else _flyout.ShowNear(Cursor.Position);
                return;
            }
            if (evt == 0x0407) { HideFlyout(); return; }

            if (evt == 0x0202 || evt == 0x0203 || evt == 0x0400 || evt == 0x0401 || evt == 0x0405)
            {
                HideFlyout();
                if (Clicked != null) Clicked(this, EventArgs.Empty);   // 左键单击/双击/键盘选择/气泡点击
            }
            else if (evt == 0x0205 || evt == 0x007B)
            {
                HideFlyout();
                if (MenuRequested != null) MenuRequested(this, EventArgs.Empty);  // 右键 / 上下文菜单
            }
        }

        public void Remove()
        {
            if (!_added) return;
            NOTIFYICONDATA d = new NOTIFYICONDATA();
            d.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
            d.hWnd = _registeredWindow;
            d.uID = 1;
            d.szTip = d.szInfo = d.szInfoTitle = "";
            Shell_NotifyIcon(NIM_DELETE, ref d);
            _added = false;
            _registeredWindow = IntPtr.Zero;
            UiLog.W("tray NIM_DELETE");
        }

        public void Dispose()
        {
            if (_disposed) return;
            Remove();
            _disposed = true;
            _form.HandleCreated -= HandleCreated;
            _form.HandleDestroyed -= HandleDestroyed;
            _form.Shown -= FormShown;
            _form.VisibleChanged -= FormVisibleChanged;
            try { if (_form != null && _form.IsHandleCreated) _hook.ReleaseHandle(); }
            catch { }
            _flyout.Dispose();
            _normalIcon.Dispose();
            _warningIcon.Dispose();
        }

        static string Trim(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max);
        }
    }
}
