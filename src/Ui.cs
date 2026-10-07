using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text;
using System.Windows.Forms;

namespace Gpm
{
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(27, 27, 27);
        public static readonly Color Card = Color.FromArgb(39, 39, 39);
        public static readonly Color Head = Color.FromArgb(46, 46, 46);
        public static readonly Color Line = Color.FromArgb(58, 58, 58);
        public static readonly Color Fg = Color.FromArgb(243, 243, 243);
        public static readonly Color Fg2 = Color.FromArgb(198, 198, 198);
        public static readonly Color Dim = Color.FromArgb(148, 148, 148);
        public static readonly Color Grid = Color.FromArgb(20, 20, 20);
        public static readonly Color Cpu = Color.FromArgb(76, 194, 255);
        public static readonly Color Mem = Color.FromArgb(185, 140, 255);
        public static readonly Color Disk = Color.FromArgb(99, 209, 122);
        public static readonly Color Net = Color.FromArgb(76, 194, 255);
        public static readonly Color Gpu = Color.FromArgb(255, 179, 71);
        public static readonly Color Temp = Color.FromArgb(255, 122, 122);
        public static readonly Color Ok = Color.FromArgb(99, 209, 122);
        public static readonly Color Bad = Color.FromArgb(255, 107, 107);
        public static readonly Color Warn = Color.FromArgb(255, 200, 90);

        public static readonly Font FBig = new Font("Segoe UI", 25f);
        public static readonly Font FMid = new Font("Segoe UI", 17f);
        public static readonly Font FBody = new Font("Segoe UI", 9.5f);
        public static readonly Font FSmall = new Font("Segoe UI", 8f);
        public static readonly Font FBold = new Font("Segoe UI", 9f, FontStyle.Bold);
        public static readonly Font FTitle = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        public static readonly Font FHead = new Font("Segoe UI", 13f, FontStyle.Bold);
    }

    /// <summary>一个监视目标的一整列（任务管理器"性能"页风格，全部自绘）</summary>
    class TargetPanel : Control
    {
        public Target T;
        /// <summary>当前布局里的列数，用于单列时限制内容最大宽度</summary>
        public int ColumnCount = 1;

        /// <summary>通道短名（列头徽标用，避免把 probe 显示成 SSH）</summary>
        public static string ChannelShort(Target t)
        {
            if (t == null) return "";
            if (t.Kind == "probe") return "probe " + t.Host + ":" + t.Port;
            if (t.Kind == "ssh") return "ssh " + t.Host;
            if (t.Kind == "local") return "本机 PDH";
            return t.Kind + " " + t.Host;
        }
        DateTime _paintedAt = DateTime.MaxValue;

        /// <summary>只有拿到新快照才重绘（否则 250ms 定时器会无谓重画整个面板）</summary>
        public void InvalidateIfNew()
        {
            Snapshot s = T.Latest;
            DateTime at = (s == null) ? DateTime.MinValue : s.At;
            if (at == _paintedAt) return;
            _paintedAt = at;
            Invalidate();
        }

        public TargetPanel(Target t)
        {
            T = t;
            DoubleBuffered = true;
            BackColor = Theme.Bg;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        // ---------- 小工具 ----------
        static void Txt(Graphics g, string s, Font f, Color c, float x, float y)
        {
            using (SolidBrush b = new SolidBrush(c)) g.DrawString(s, f, b, x, y);
        }

        static void TxtR(Graphics g, string s, Font f, Color c, float right, float y)
        {
            SizeF sz = g.MeasureString(s, f);
            Txt(g, s, f, c, right - sz.Width, y);
        }

        static string FmtBytes(double b)
        {
            if (double.IsNaN(b) || b <= 0) return "0";
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            int i = 0; double v = b;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return (v >= 100 || i <= 1 ? v.ToString("0") : v.ToString("0.0")) + " " + u[i];
        }

        static string FmtRate(double bps)
        {
            if (bps >= 1048576) return (bps / 1048576).ToString("0.0") + " MB/s";
            if (bps >= 1024) return (bps / 1024).ToString("0") + " KB/s";
            return bps.ToString("0") + " B/s";
        }

        static string FmtMbps(double bps)
        {
            double mb = bps * 8 / 1e6;
            if (mb >= 1000) return (mb / 1000).ToString("0.00") + " Gbps";
            if (mb >= 1) return mb.ToString("0.0") + " Mbps";
            return (mb * 1000).ToString("0") + " Kbps";
        }

        static string EngineLabel(string k)
        {
            switch (k)
            {
                case "3d": return "3D";
                case "high priority 3d": return "3D（高优先级）";
                case "compute": return "计算";
                case "high priority compute": return "计算（高优先级）";
                case "copy": return "复制";
                case "video decode": return "视频解码";
                case "video encode": return "视频编码";
                case "video codec": return "视频编解码";
                case "video codec engine": return "视频编解码";
                case "video jpeg": return "JPEG 解码";
                case "video processor": return "视频处理";
                case "video processing": return "视频处理";
                case "legacy overlay": return "传统覆盖";
                case "optical flow": return "光流";
                case "sc decode": return "安全解码";
                case "sc encode": return "安全编码";
                case "security": return "安全";
                case "timer": return "计时器";
                default: return k;
            }
        }

        // ---------- 绘制基元 ----------
        static Rectangle Card(Graphics g, Rectangle r, string title, string note)
        {
            using (SolidBrush b = new SolidBrush(Theme.Card)) g.FillRectangle(b, r);
            using (Pen p = new Pen(Theme.Line)) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
            Rectangle head = new Rectangle(r.X + 1, r.Y + 1, r.Width - 2, 24);
            using (SolidBrush b = new SolidBrush(Theme.Head)) g.FillRectangle(b, head);
            Txt(g, title, Theme.FTitle, Theme.Fg, r.X + 10, r.Y + 5);
            if (!string.IsNullOrEmpty(note)) TxtR(g, note, Theme.FSmall, Theme.Dim, r.Right - 10, r.Y + 7);
            return new Rectangle(r.X + 11, r.Y + 30, r.Width - 22, r.Height - 40);
        }

        static void Graph(Graphics g, Rectangle r, List<double> data, Color color, double maxFixed)
        {
            if (r.Width < 8 || r.Height < 8) return;
            using (SolidBrush b = new SolidBrush(Theme.Grid)) g.FillRectangle(b, r);
            using (Pen gp = new Pen(Color.FromArgb(30, 255, 255, 255)))
            {
                for (int i = 1; i < 4; i++) { int y = r.Y + r.Height * i / 4; g.DrawLine(gp, r.Left, y, r.Right, y); }
                for (int i = 1; i < 8; i++) { int x = r.Left + r.Width * i / 8; g.DrawLine(gp, x, r.Top, x, r.Bottom); }
            }
            if (data == null || data.Count < 2) return;
            double max = maxFixed;
            if (max <= 0)
            {
                foreach (double v in data) if (v > max) max = v;
                max *= 1.25;
            }
            if (max <= 0) max = 1;
            int n = data.Count;
            float step = (float)r.Width / (n - 1);
            PointF[] pts = new PointF[n];
            for (int i = 0; i < n; i++)
            {
                double v = data[i];
                if (double.IsNaN(v) || v < 0) v = 0;
                if (v > max) v = max;
                pts[i] = new PointF(r.Left + i * step, r.Bottom - 1 - (float)(v / max * (r.Height - 3)));
            }
            PointF[] fill = new PointF[n + 2];
            Array.Copy(pts, fill, n);
            fill[n] = new PointF(pts[n - 1].X, r.Bottom);
            fill[n + 1] = new PointF(pts[0].X, r.Bottom);
            using (LinearGradientBrush lg = new LinearGradientBrush(new Rectangle(r.X, r.Y - 1, r.Width, r.Height + 1),
                       Color.FromArgb(140, color), Color.FromArgb(12, color), LinearGradientMode.Vertical))
                g.FillPolygon(lg, fill);
            using (Pen lp = new Pen(color, 1.4f)) g.DrawLines(lp, pts);
        }

        static void Bar(Graphics g, Rectangle r, double pct, Color color)
        {
            using (SolidBrush b = new SolidBrush(Theme.Grid)) g.FillRectangle(b, r);
            int w = (int)Math.Round(r.Width * Math.Min(100, Math.Max(0, pct)) / 100.0);
            if (w > 0)
            {
                using (SolidBrush b = new SolidBrush(color)) g.FillRectangle(b, r.X, r.Y, w, r.Height);
            }
        }

        static void Chip(Graphics g, ref float x, float y, string text, Color color)
        {
            SizeF sz = g.MeasureString(text, Theme.FSmall);
            Rectangle r = new Rectangle((int)x, (int)y, (int)sz.Width + 14, 20);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(31, 31, 31))) g.FillRectangle(b, r);
            using (Pen p = new Pen(Theme.Line)) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
            Txt(g, text, Theme.FSmall, color, r.X + 7, r.Y + 3);
            x = r.Right + 6;
        }

        // ---------- 主绘制 ----------
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (SolidBrush b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, ClientRectangle);

            Snapshot s = T.Latest;
            int pad = 8;
            int maxW = ColumnCount <= 1 ? 1240 : 100000;   // 只显示一列时别把卡片拉得太宽
            int w = Math.Min(Width - pad * 2, maxW);
            int x0 = pad + Math.Max(0, (Width - pad * 2 - w) / 2);
            float y = pad;

            // ---- 顶部：名称 / 通道 / 机器信息 ----
            string title = T.Name;
            if (s != null && s.Host.Length > 0 && s.Host != T.Name) title = T.Name + "  ·  " + s.Host;
            Color dot = (s == null) ? Theme.Dim : (s.Ok ? Theme.Ok : Theme.Bad);
            using (SolidBrush b = new SolidBrush(dot)) g.FillEllipse(b, x0, y + 4, 9, 9);
            Txt(g, title, Theme.FHead, Theme.Fg, x0 + 15, y - 2);

            string chan = ChannelShort(T);
            float cx = x0 + 15 + g.MeasureString(title, Theme.FHead).Width + 10;
            SizeF cs = g.MeasureString(chan, Theme.FSmall);
            Rectangle cr = new Rectangle((int)cx, (int)y + 2, (int)cs.Width + 14, 18);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(51, 51, 51))) g.FillRectangle(b, cr);
            using (Pen p = new Pen(Theme.Line)) g.DrawRectangle(p, cr.X, cr.Y, cr.Width - 1, cr.Height - 1);
            Txt(g, chan, Theme.FSmall, Theme.Fg2, cr.X + 7, cr.Y + 1);

            if (s != null)
            {
                string meta = (s.Os.Length > 0 ? s.Os : "") + (s.Uptime.Length > 0 ? "   运行 " + s.Uptime : "");
                TxtR(g, meta, Theme.FSmall, Theme.Dim, Width - pad, y + 3);
                TxtR(g, "采集 " + s.CollectMs + " ms", Theme.FSmall, Theme.Dim, Width - pad, y + 17);
            }
            y += 34;

            // ---- 采集失败时先在顶部画一条错误带，再按权重分配四张卡片的高度（避免卡片被挤出屏幕）----
            if (s != null && !s.Ok)
            {
                Rectangle rerr = new Rectangle(x0, (int)y, w, 78);
                Rectangle inner2 = Card(g, rerr, "采集失败 · 该目标数据暂不可用", "");
                Txt(g, s.Error, Theme.FBody, Theme.Bad, inner2.X, inner2.Y + 4);
                y += 78 + 8;
            }

            float avail = Height - y - pad - 3 * 8;
            if (avail < 200) avail = 200;
            double[] wt = { 1.32, 1.02, 0.96, 1.22 };
            double sum = 0; foreach (double v in wt) sum += v;
            float[] hh = new float[4];
            for (int i = 0; i < 4; i++) hh[i] = (float)(avail * wt[i] / sum);

            if (s == null)
            {
                Rectangle r = new Rectangle(x0, (int)y, w, (int)hh[0]);
                Rectangle inner = Card(g, r, "状态", "");
                Txt(g, "正在采集…", Theme.FBody, Theme.Dim, inner.X, inner.Y + 4);
                return;
            }

            // ============ CPU ============
            Rectangle rc = new Rectangle(x0, (int)y, w, (int)hh[0]);
            Rectangle ci = Card(g, rc, "CPU", s.CpuName);
            int halfW = ci.Width / 2 - 8;
            Rectangle cLeft = new Rectangle(ci.X, ci.Y, halfW, ci.Height);
            Rectangle cRight = new Rectangle(ci.X + halfW + 16, ci.Y, ci.Width - halfW - 16, ci.Height);
            Txt(g, s.CpuPct.ToString("0") + "%", Theme.FBig, Theme.Fg, cLeft.X, cLeft.Y - 4);
            string cpuSub = (s.CpuMhz > 0 ? (s.CpuMhz / 1000.0).ToString("0.00") + " GHz" : "");
            Txt(g, cpuSub, Theme.FSmall, Theme.Fg2, cLeft.X + 4, cLeft.Y + 40);
            Graph(g, new Rectangle(cLeft.X, cLeft.Y + 58, cLeft.Width, cLeft.Height - 58), T.HCpu, Theme.Cpu, 100);

            Txt(g, "逻辑处理器 " + s.Cores.Count, Theme.FSmall, Theme.Dim, cRight.X, cRight.Y - 2);
            if (s.Cores.Count > 0 && cRight.Width > 60 && cRight.Height > 60)
            {
                int cols = Math.Max(4, Math.Min(8, cRight.Width / 58));
                int rows = (s.Cores.Count + cols - 1) / cols;
                int cw = (cRight.Width - (cols - 1) * 4) / cols;
                int ch = Math.Min(34, (cRight.Height - 16 - (rows - 1) * 4) / rows);
                if (ch < 12) ch = 12;
                for (int i = 0; i < s.Cores.Count; i++)
                {
                    int cx2 = cRight.X + (i % cols) * (cw + 4);
                    int cy2 = cRight.Y + 16 + (i / cols) * (ch + 4);
                    if (cy2 + ch > cRight.Bottom) break;
                    List<double> hist = (i < T.HCores.Count) ? T.HCores[i] : null;
                    Graph(g, new Rectangle(cx2, cy2, cw, ch), hist, Theme.Cpu, 100);
                    Txt(g, (i + 1).ToString(), Theme.FSmall, Theme.Dim, cx2 + 2, cy2 + ch - 12);
                }
            }
            y += hh[0] + 8;

            // ============ 内存 ============
            Rectangle rm = new Rectangle(x0, (int)y, w, (int)hh[1]);
            Rectangle mi = Card(g, rm, "内存", s.MemCommittedBytes > 0 ? "已提交 " + FmtBytes(s.MemCommittedBytes) : "");
            int mhalf = mi.Width / 2 - 8;
            Rectangle mLeft = new Rectangle(mi.X, mi.Y, mhalf, mi.Height);
            Rectangle mRight = new Rectangle(mi.X + mhalf + 16, mi.Y, mi.Width - mhalf - 16, mi.Height);
            Txt(g, s.MemPct.ToString("0") + "%", Theme.FBig, Theme.Fg, mLeft.X, mLeft.Y - 4);
            Txt(g, FmtBytes(s.MemUsedBytes) + " / " + FmtBytes(s.MemTotalBytes) + "   可用 " + FmtBytes(s.MemAvailBytes),
                Theme.FSmall, Theme.Fg2, mLeft.X + 4, mLeft.Y + 40);
            Bar(g, new Rectangle(mLeft.X, mLeft.Y + 58, mLeft.Width, 8), s.MemPct, Theme.Mem);
            Graph(g, new Rectangle(mLeft.X, mLeft.Y + 74, mLeft.Width, mLeft.Height - 74), T.HMem, Theme.Mem, 100);

            Txt(g, "磁盘容量", Theme.FSmall, Theme.Dim, mRight.X, mRight.Y - 2);
            float vy = mRight.Y + 16;
            foreach (Volume v in s.Volumes)
            {
                if (vy + 30 > mRight.Bottom) break;
                double used = v.SizeBytes - v.FreeBytes;
                double pct = v.SizeBytes > 0 ? used / v.SizeBytes * 100 : 0;
                Txt(g, v.Drive, Theme.FSmall, Theme.Fg2, mRight.X, vy);
                TxtR(g, FmtBytes(v.FreeBytes) + " 可用 / " + FmtBytes(v.SizeBytes) + "  " + pct.ToString("0") + "%",
                    Theme.FSmall, Theme.Dim, mRight.Right, vy);
                Bar(g, new Rectangle(mRight.X, (int)vy + 15, mRight.Width, 6), pct, pct > 92 ? Theme.Bad : Theme.Disk);
                vy += 28;
            }
            y += hh[1] + 8;

            // ============ 磁盘 / 网络 ============
            Rectangle rd = new Rectangle(x0, (int)y, w, (int)hh[2]);
            Rectangle di = Card(g, rd, "磁盘 / 网络", "队列 " + s.DiskQueue.ToString("0.00"));
            int dhalf = di.Width / 2 - 8;
            Rectangle dLeft = new Rectangle(di.X, di.Y, dhalf, di.Height);
            Rectangle nRight = new Rectangle(di.X + dhalf + 16, di.Y, di.Width - dhalf - 16, di.Height);
            Txt(g, s.DiskActive.ToString("0") + "%", Theme.FMid, Theme.Fg, dLeft.X, dLeft.Y - 2);
            Txt(g, "读 " + FmtRate(s.DiskReadBps) + "   写 " + FmtRate(s.DiskWriteBps), Theme.FSmall, Theme.Fg2, dLeft.X + 4, dLeft.Y + 24);
            Graph(g, new Rectangle(dLeft.X, dLeft.Y + 42, dLeft.Width, dLeft.Height - 42), T.HDiskR, Theme.Disk, 0);
            Graph(g, new Rectangle(dLeft.X, dLeft.Y + 42, dLeft.Width, dLeft.Height - 42), T.HDiskW, Theme.Gpu, 0);

            Txt(g, FmtMbps(s.NetRxBps + s.NetTxBps), Theme.FMid, Theme.Fg, nRight.X, nRight.Y - 2);
            string nic = s.NetAdapter.Length > 34 ? s.NetAdapter.Substring(0, 34) : s.NetAdapter;
            Txt(g, "↓ " + FmtMbps(s.NetRxBps) + "   ↑ " + FmtMbps(s.NetTxBps), Theme.FSmall, Theme.Fg2, nRight.X + 4, nRight.Y + 24);
            Txt(g, nic, Theme.FSmall, Theme.Dim, nRight.X + 4, nRight.Y + 38);
            Graph(g, new Rectangle(nRight.X, nRight.Y + 54, nRight.Width, nRight.Height - 54), T.HNetR, Theme.Net, 0);
            Graph(g, new Rectangle(nRight.X, nRight.Y + 54, nRight.Width, nRight.Height - 54), T.HNetT, Theme.Mem, 0);
            y += hh[2] + 8;

            // ============ GPU ============
            Rectangle rg = new Rectangle(x0, (int)y, w, (int)hh[3]);
            Rectangle gi = Card(g, rg, "GPU", s.GpuName);
            int ghalf = gi.Width / 2 - 8;
            Rectangle gLeft = new Rectangle(gi.X, gi.Y, ghalf, gi.Height);
            Rectangle gRight = new Rectangle(gi.X + ghalf + 16, gi.Y, gi.Width - ghalf - 16, gi.Height);
            Txt(g, s.HasGpu ? s.GpuPct.ToString("0") + "%" : "–", Theme.FBig, Theme.Fg, gLeft.X, gLeft.Y - 4);
            Txt(g, "显存 " + FmtBytes(s.VramUsed) + (s.VramShared > 0 ? "  共享 " + FmtBytes(s.VramShared) : ""),
                Theme.FSmall, Theme.Fg2, gLeft.X + 4, gLeft.Y + 40);
            Graph(g, new Rectangle(gLeft.X, gLeft.Y + 56, gLeft.Width, gLeft.Height - 56), T.HGpu, Theme.Gpu, 100);

            float ey = gRight.Y - 2;
            Txt(g, "引擎", Theme.FSmall, Theme.Dim, gRight.X, ey);
            ey += 14;
            List<KeyValuePair<string, double>> eng = new List<KeyValuePair<string, double>>(s.Engines);
            eng.Sort(delegate(KeyValuePair<string, double> a, KeyValuePair<string, double> b) { return b.Value.CompareTo(a.Value); });
            int shown = 0;
            foreach (KeyValuePair<string, double> kv in eng)
            {
                if (ey + 15 > gRight.Bottom - 46 || shown >= 6) break;
                string label = EngineLabel(kv.Key);
                Txt(g, label, Theme.FSmall, Theme.Fg2, gRight.X, ey);
                int trackX = gRight.X + 88;
                int trackW = gRight.Width - 88 - 42;
                if (trackW > 20)
                {
                    Bar(g, new Rectangle(trackX, (int)ey + 4, trackW, 6), kv.Value, Theme.Gpu);
                }
                TxtR(g, kv.Value.ToString("0") + "%", Theme.FSmall, Theme.Dim, gRight.Right, ey);
                ey += 16;
                shown++;
            }

            // 温度 / 风扇 / 功耗 芯片
            float chx = gRight.X;
            float chy = gRight.Bottom - 24;
            if (!double.IsNaN(s.GpuTempC))
            {
                Color tc = s.GpuTempC >= 85 ? Theme.Bad : (s.GpuTempC >= 70 ? Theme.Warn : Theme.Ok);
                Chip(g, ref chx, chy, "GPU " + s.GpuTempC.ToString("0") + " °C", tc);
            }
            if (!double.IsNaN(s.SysTempC)) Chip(g, ref chx, chy, "系统 " + s.SysTempC.ToString("0") + " °C", Theme.Fg2);
            if (!double.IsNaN(s.GpuFanRpm)) Chip(g, ref chx, chy, "风扇 " + s.GpuFanRpm.ToString("0") + " RPM", Theme.Fg2);
            if (!double.IsNaN(s.GpuPowerPct)) Chip(g, ref chx, chy, "功耗 " + s.GpuPowerPct.ToString("0.0") + "% TDP", Theme.Fg2);
            if (chx == gRight.X) Chip(g, ref chx, chy, "无温度/功耗传感器", Theme.Dim);

            y += hh[3] + 8;

            // 底部状态行
            if (!s.Ok && s.Error.Length > 0)
                Txt(g, "采集失败：" + s.Error, Theme.FSmall, Theme.Bad, x0, Height - pad - 14);
            else
                Txt(g, "最后更新 " + s.At.ToString("HH:mm:ss") + "   通道 " + (T.Kind == "local" ? "本机 PDH" : "SSH + 远端 PDH"),
                    Theme.FSmall, Theme.Dim, x0, Height - pad - 14);
        }
    }

    class MainForm : Form
    {
        List<Target> _all;
        List<Target> _targets = new List<Target>();
        readonly List<TargetPanel> _panels = new List<TargetPanel>();
        readonly Timer _timer = new Timer();
        readonly string _cfgPath;
        readonly bool _forceAll;
        readonly TableLayoutPanel _tlp = new TableLayoutPanel();
        Label _lblTitle;
        Label _overviewCount;
        Button _historyButton;
        FaultHistoryForm _historyForm;
        readonly AlertMonitor _alerts=new AlertMonitor();
        readonly List<AlertNotice> _alertQueue=new List<AlertNotice>();
        DateTime _nextAlert=DateTime.MinValue;
        readonly Font _dashboardTitleFont=new Font("Microsoft YaHei UI",17,FontStyle.Bold);
        readonly Font _dashboardBodyFont=new Font("Microsoft YaHei UI",9);
        TrayIcon _tray;
        bool _trayHintShown;
        bool _reallyExit;
        WidgetForm _widget;
        WidgetPreferences _widgetPreferences;
        string _widgetPath;
        public bool RestoreWidgetOnShow=true;
        /// <summary>点 ✕ / 最小化时是否收进托盘（设置窗口里可改）</summary>
        public static bool CloseToTray
        {
            get { return Config.CloseToTray; }
            set { Config.CloseToTray = value; }
        }
        ComboBox _cmbTarget;
        bool _suppressCombo;
        readonly List<Target> _cmbItems = new List<Target>();
        CheckBox _chkLocal;

        public MainForm(List<Target> targets, string cfgPath, bool forceAll)
        {
            _all = targets;
            _cfgPath = cfgPath;
            _forceAll = forceAll;
            _widgetPath=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(cfgPath)),"widget.json");
            _widgetPreferences=WidgetPreferences.Load(_widgetPath);
            Text = "GPU PC 性能监视 · 性能总览";
            Icon = AppIcons.Load("monitor", SystemInformation.IconSize);
            BackColor = Color.FromArgb(16,23,34);
            ForeColor = Theme.Fg;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1360, 900);
            MinimumSize = new Size(900, 600);
            Font = Theme.FBody;

            BuildDashboardHeader();

            _tlp.Dock = DockStyle.Fill;
            _tlp.BackColor = Color.FromArgb(16,23,34);
            _tlp.Padding = new Padding(12,0,12,8);
            _tlp.RowCount = 1;
            _tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(_tlp);
            _tlp.BringToFront();
            RebuildLayout();

            // ---- 原生托盘图标（Shell_NotifyIcon）：最小化/关闭可选收进托盘，悬停显示关键指标 ----
            _tray = new TrayIcon(this, "GPU PC 性能监视");
            _tray.Clicked += delegate { RestoreFromTray(); };
            _tray.MenuRequested += delegate { ShowTrayMenu(); };
            _tray.Unavailable += delegate { if (!Visible) RestoreFromTray(); };
            Resize += delegate
            {
                if (CloseToTray && WindowState == FormWindowState.Minimized)
                {
                    TryHideToTray();
                }
            };

            _timer.Interval = 250;
            _timer.Tick += delegate { Tick(); };
            _timer.Start();
        }

        void BuildDashboardHeader()
        {
            Panel header=new Panel{Dock=DockStyle.Top,Height=120,Width=ClientSize.Width,BackColor=Color.FromArgb(20,29,42)};
            header.Paint+=delegate(object sender,PaintEventArgs e){e.Graphics.DrawIcon(Icon,new Rectangle(24,19,32,32));};
            _lblTitle=new Label{Text="性能总览",Font=_dashboardTitleFont,ForeColor=Theme.Fg,Location=new Point(72,12),Size=new Size(390,34)};header.Controls.Add(_lblTitle);
            header.Controls.Add(new Label{Text="实时掌握设备负载与运行状态",Font=_dashboardBodyFont,ForeColor=Color.FromArgb(146,164,184),Location=new Point(74,47),Size=new Size(410,23)});
            _historyButton=new Button{Text="故障记录 (0)",FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(34,49,68),ForeColor=Theme.Fg,Font=_dashboardBodyFont,Location=new Point(ClientSize.Width-384,22),Size=new Size(122,35),Anchor=AnchorStyles.Top|AnchorStyles.Right};
            _historyButton.FlatAppearance.BorderColor=Color.FromArgb(64,82,104);_historyButton.Click+=delegate{OpenHistory();};header.Controls.Add(_historyButton);
            Button widget=new Button{Text="小部件",FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(34,49,68),ForeColor=Theme.Fg,Font=_dashboardBodyFont,Location=new Point(ClientSize.Width-250,22),Size=new Size(102,35),Anchor=AnchorStyles.Top|AnchorStyles.Right};
            widget.FlatAppearance.BorderColor=Color.FromArgb(64,82,104);widget.Click+=delegate{ShowWidget();};header.Controls.Add(widget);
            Button settings=new Button{Text="连接设置",FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(94,200,241),ForeColor=Color.FromArgb(14,30,43),Font=_dashboardBodyFont,Location=new Point(ClientSize.Width-136,22),Size=new Size(112,35),Anchor=AnchorStyles.Top|AnchorStyles.Right};
            settings.FlatAppearance.BorderSize=0;settings.Click+=delegate{OpenSettings();};header.Controls.Add(settings);
            Panel toolbar=new Panel{Dock=DockStyle.Bottom,Height=48,Width=ClientSize.Width,BackColor=Color.FromArgb(22,32,46)};
            toolbar.Controls.Add(new Label{Text="监控设备",Font=_dashboardBodyFont,ForeColor=Color.FromArgb(146,164,184),Location=new Point(24,15),Size=new Size(76,23)});
            _cmbTarget=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(34,47,64),ForeColor=Theme.Fg,Font=_dashboardBodyFont,Location=new Point(108,9),Width=350};
            _cmbTarget.DrawMode=DrawMode.OwnerDrawFixed;_cmbTarget.ItemHeight=22;
            _cmbTarget.DrawItem+=delegate(object sender,DrawItemEventArgs e)
            {
                Color background=(e.State&DrawItemState.Selected)!=0?Color.FromArgb(43,70,91):Color.FromArgb(34,47,64);
                using(var brush=new SolidBrush(background))e.Graphics.FillRectangle(brush,e.Bounds);
                string text=e.Index>=0&&e.Index<_cmbTarget.Items.Count?_cmbTarget.Items[e.Index].ToString():"选择监控设备";
                TextRenderer.DrawText(e.Graphics,text,_dashboardBodyFont,new Rectangle(e.Bounds.Left+8,e.Bounds.Top+2,e.Bounds.Width-12,e.Bounds.Height-2),Theme.Fg,TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPadding);
            };
            _cmbTarget.SelectedIndexChanged+=delegate
            {
                if(_suppressCombo)return;int index=_cmbTarget.SelectedIndex;if(index<0||index>=_cmbItems.Count)return;
                Target selected=_cmbItems[index];foreach(Target target in _all)if(target.Kind!="local")target.Enabled=(target==selected);
                try{Config.Save(_cfgPath,_all);}catch{}RebuildLayout();
            };toolbar.Controls.Add(_cmbTarget);
            _chkLocal=new CheckBox{Text="同时显示本机",Font=_dashboardBodyFont,ForeColor=Theme.Fg2,AutoSize=true,Location=new Point(480,13),Checked=LocalTarget().Enabled};
            _chkLocal.CheckedChanged+=delegate{LocalTarget().Enabled=_chkLocal.Checked;try{Config.Save(_cfgPath,_all);}catch{}RebuildLayout();};toolbar.Controls.Add(_chkLocal);
            _overviewCount=new Label{Text="数据自动更新",Font=_dashboardBodyFont,ForeColor=Color.FromArgb(146,164,184),Location=new Point(ClientSize.Width-246,14),Size=new Size(222,22),TextAlign=ContentAlignment.MiddleRight,Anchor=AnchorStyles.Top|AnchorStyles.Right};toolbar.Controls.Add(_overviewCount);
            header.Controls.Add(toolbar);Controls.Add(header);
        }

        public void ShowWidget()
        {
            if(_widget==null||_widget.IsDisposed)_widget=new WidgetForm(delegate{return _targets;},RestoreFromTray,_widgetPreferences,_widgetPath);
            _widgetPreferences.Enabled=true;_widgetPreferences.Save(_widgetPath);
            _widget.Show();Hide();
        }
        void OpenHistory()
        {
            if(_historyForm==null||_historyForm.IsDisposed)_historyForm=new FaultHistoryForm();
            _historyForm.Show();_historyForm.WindowState=FormWindowState.Normal;_historyForm.Activate();
        }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if(RestoreWidgetOnShow&&_widgetPreferences.Enabled)BeginInvoke((MethodInvoker)delegate{ShowWidget();});
        }

        /// <summary>拿到"本机"目标；配置里没有就补一个（默认不启用）</summary>
        Target LocalTarget()
        {
            foreach (Target t in _all) if (t.Kind == "local") return t;
            Target nt = new Target();
            nt.Name = "本机";
            nt.Kind = "local";
            nt.IntervalSec = 1;
            nt.Enabled = false;
            _all.Insert(0, nt);
            return nt;
        }

        /// <summary>按"已启用"的目标重建两列/单列布局（勾选框切换时调用）</summary>
        void RebuildLayout()
        {
            foreach (TargetPanel p in _panels) { _tlp.Controls.Remove(p); p.Dispose(); }
            _panels.Clear();
            while(_tlp.Controls.Count>0)_tlp.Controls[0].Dispose();
            while (_tlp.ColumnStyles.Count > 0) _tlp.ColumnStyles.RemoveAt(0);
            while (_tlp.RowStyles.Count > 0) _tlp.RowStyles.RemoveAt(0);
            _tlp.RowCount = 1;
            _tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            List<Target> act = new List<Target>();
            foreach (Target t in _all) if (t.Enabled || _forceAll) act.Add(t);
            _targets = act;

            int cols = act.Count > 0 ? act.Count : 1;
            _tlp.ColumnCount = cols;
            for (int i = 0; i < cols; i++) _tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cols));
            for (int i = 0; i < act.Count; i++)
            {
                DashboardPanel p = new DashboardPanel(act[i]);
                p.ColumnCount = act.Count;
                DashboardViewport host=new DashboardViewport(p){Dock=DockStyle.Fill,Margin=new Padding(0)};
                _panels.Add(p);
                _tlp.Controls.Add(host, i, 0);
                host.Arrange();
            }
            if (act.Count == 0)
            {
                Label l = new Label();
                l.Text = "没有启用的目标\r\n\r\n勾选上方“包含本机”，或在 config.json 里给远程目标设 enabled: true";
                l.ForeColor = Theme.Dim;
                l.Font = Theme.FBody;
                l.Dock = DockStyle.Fill;
                l.TextAlign = ContentAlignment.MiddleCenter;
                _tlp.Controls.Add(l, 0, 0);
            }
            // 通道下拉：列出 config.json 里所有远端目标（含未启用的兜底通道）
            if (_cmbTarget != null)
            {
                _suppressCombo = true;
                _cmbItems.Clear();
                _cmbTarget.Items.Clear();
                foreach (Target t in _all)
                {
                    if (t.Kind == "local") continue;
                    _cmbItems.Add(t);
                    _cmbTarget.Items.Add(t.Name + "  ·  " + (t.Kind=="probe"?"探针连接":"SSH 连接") + (t.Enabled ? "" : "   (未启用)"));
                }
                int sel = -1;
                for (int i = 0; i < _cmbItems.Count; i++) if (_cmbItems[i].Enabled) { sel = i; break; }
                if (_cmbTarget.Items.Count > 0) _cmbTarget.SelectedIndex = (sel >= 0 ? sel : 0);
                _suppressCombo = false;
            }
            if (_overviewCount != null) _overviewCount.Text = act.Count+" 台设备 · 数据自动更新";
            if (_tray != null) _tray.Update(TrayText());
            foreach (TargetPanel p in _panels) p.InvalidateIfNew();
        }

        /// <summary>通道短名（列头徽标与下拉都用它，避免把 probe 显示成 SSH）</summary>
        static string ChannelShort(Target t)
        {
            if (t.Kind == "probe") return "probe " + t.Host + ":" + t.Port;
            if (t.Kind == "ssh") return "ssh " + t.Host;
            if (t.Kind == "local") return "本机 PDH";
            return t.Kind + " " + t.Host;
        }

        void OpenSettings()
        {
            using (SettingsForm f = new SettingsForm(_all, _cfgPath))
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    UiLog.W("settings changed, restarting");
                    _reallyExit = true;
                    Application.Restart();
                }
            }
        }

        void ShowTrayMenu()
        {
            try
            {
                _tray.HideFlyout();
                int choice = TrayMenu.Show(this, "显示窗口", "设置…", "打开日志目录", "-", "退出", "精简小部件", "故障记录…");
                if (choice == 1) RestoreFromTray();
                else if (choice == 2) { RestoreFromTray(); OpenSettings(); }
                else if (choice == 3) System.Diagnostics.Process.Start("explorer.exe", Config.DataDir);
                else if (choice == 5) ExitApplication();
                else if (choice == 6) ShowWidget();
                else if (choice == 7) OpenHistory();
            }
            catch (Exception ex) { UiLog.W("tray menu failed: " + ex.Message); RestoreFromTray(); }
        }

        public void ExitApplication()
        {
            _reallyExit = true;
            Close();
        }

        bool TryHideToTray()
        {
            if (_tray == null || !_tray.EnsureAdded())
            {
                UiLog.W("tray unavailable: keeping window accessible on taskbar");
                return false;
            }
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.Balloon("已收进托盘", "鼠标悬停看指标，双击图标恢复窗口；退出请右键托盘图标");
            }
            return true;
        }

        void RestoreFromTray()
        {
            _widgetPreferences.Enabled=false;_widgetPreferences.Save(_widgetPath);
            if(_widget!=null)_widget.Hide();
            if (_tray != null) _tray.HideFlyout();
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        /// <summary>分行关键指标供 Windows 标准托盘提示使用（最多 127 字符）。</summary>
        string TrayText()
        {
            StringBuilder b = new StringBuilder();
            foreach (Target t in _targets)
            {
                Snapshot s = t.Latest;
                if (b.Length > 0) b.Append("\n\n");
                string name=t.Name??"设备";if(name.Length>20)name=name.Substring(0,19)+"…";
                b.Append(name).Append(" · ").Append(s == null ? "采集中" : s.Ok ? "在线" : "连接异常").Append('\n');
                if (s == null) { b.Append("  正在等待首次采样…"); continue; }
                if (!s.Ok) { b.Append("  暂无有效指标，请查看主窗口\n  最近检查 ").Append(s.At.ToString("HH:mm:ss")); continue; }
                b.Append("CPU ").Append(s.CpuPct.ToString("0.0")).Append("%\n");
                b.Append("内存 ").Append(s.MemPct.ToString("0.0")).Append("% · ").Append((s.MemUsedBytes/1073741824.0).ToString("0.0")).Append("/").Append((s.MemTotalBytes/1073741824.0).ToString("0.0")).Append(" GB\n");
                b.Append("GPU ").Append(s.HasGpu ? s.GpuPct.ToString("0.0")+"%" : "未检测到");
                if (!double.IsNaN(s.GpuTempC)) b.Append(" · ").Append(s.GpuTempC.ToString("0")).Append(" °C");
                b.Append("\n网络 ↓").Append(Rate(s.NetRxBps)).Append(" ↑").Append(Rate(s.NetTxBps));
                b.Append("\n更新 ").Append(s.At.ToString("HH:mm:ss"));
            }
            string txt = b.ToString();
            if (txt.Length == 0) txt = "GPU PC 性能监视";
            return txt;
        }

        static string Rate(double bytes) { return bytes >= 1048576 ? (bytes/1048576).ToString("0.0")+" MB/s" : (bytes/1024).ToString("0.0")+" KB/s"; }

        static void App(List<double> list, double v)
        {
            list.Add(double.IsNaN(v) ? 0 : v);
            while (list.Count > Target.MaxPoints) list.RemoveAt(0);
        }

        void Tick()
        {
            var records=FaultHistory.Records();int recordCount=records.Length;
            if(_historyButton!=null){string label="故障记录 ("+recordCount+")";if(_historyButton.Text!=label){_historyButton.Text=label;_historyButton.ForeColor=recordCount>0?Theme.Warn:Theme.Fg;}}
            foreach(var target in _all){var notice=_alerts.Observe(target,target.Latest,DateTime.UtcNow);if(notice!=null)_alertQueue.Add(notice);}
            if(_alertQueue.Count>0&&DateTime.UtcNow>=_nextAlert)
            {
                var first=_alertQueue[0];var text=new List<string>();foreach(var notice in _alertQueue)text.Add(notice.Target.Name+"："+notice.Message);
                string message=String.Join("\n",text.ToArray());
                if(_tray!=null)_tray.Balloon("设备状态提醒",message);
                if(_widget!=null&&!_widget.IsDisposed&&_widget.Visible)_widget.ShowAlert(first.Target,first.Message);
                _alertQueue.Clear();_nextAlert=DateTime.UtcNow.AddSeconds(15);
            }
            foreach (Target t in _targets)
            {
                Snapshot s = t.Latest;
                if (s == null) continue;
                if (s.At == t.LastAppended) continue;
                t.LastAppended = s.At;
                App(t.HCpu, s.CpuPct);
                App(t.HMem, s.MemPct);
                App(t.HDiskR, s.DiskReadBps);
                App(t.HDiskW, s.DiskWriteBps);
                App(t.HDiskA, s.DiskActive);
                App(t.HNetR, s.NetRxBps);
                App(t.HNetT, s.NetTxBps);
                App(t.HGpu, s.GpuPct);
                App(t.HTemp, s.GpuTempC);
                while (t.HCores.Count < s.Cores.Count) t.HCores.Add(new List<double>());
                for (int i = 0; i < s.Cores.Count && i < t.HCores.Count; i++) App(t.HCores[i], s.Cores[i]);
            }
            if (_tray != null)
            {
                _tray.Update(TrayText());
                _tray.SetWarning(_targets.Exists(delegate(Target t) { return t.Latest != null && !t.Latest.Ok; }));
            }
            foreach (TargetPanel p in _panels) p.InvalidateIfNew();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x11 || m.Msg == 0x16)
                Program.RuntimeLog("session message=" + m.Msg + " wParam=" + m.WParam + " lParam=" + m.LParam);
            if (SingleInstance.RestoreMessage != 0 && m.Msg == SingleInstance.RestoreMessage)
            {
                RestoreFromTray();
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Program.RuntimeLog("window close reason=" + e.CloseReason + " explicitExit=" + _reallyExit);
            UiLog.W("window close: reason=" + e.CloseReason + " closeToTray=" + CloseToTray + " explicitExit=" + _reallyExit);
            // 点 ✕ = 收进托盘（真正退出走托盘菜单的"退出"）
            if (CloseToTray && !_reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                if (!TryHideToTray()) WindowState = FormWindowState.Minimized;
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            if(_historyForm!=null)_historyForm.Dispose();
            _dashboardTitleFont.Dispose();_dashboardBodyFont.Dispose();
            if(_widget!=null){_widget.Close();_widget.Dispose();}
            if (Icon != null) Icon.Dispose();
            if (_tray != null) { _tray.Remove(); _tray.Dispose(); }
            base.OnFormClosed(e);
        }
    }
}
