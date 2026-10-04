using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Gpm
{
    static class SingleInstance
    {
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
        public static readonly int RestoreMessage=RegisterWindowMessage("GPUPCMGT.Monitor.RestoreWindow");
        public static void ActivateExisting()
        {
            if(RestoreMessage!=0)PostMessage(new IntPtr(0xFFFF),RestoreMessage,IntPtr.Zero,IntPtr.Zero);
        }
    }
    static class AppIcons
    {
        public static Icon Load(string name, Size size)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Gpm." + name + ".ico"))
            {
                if (s == null) return new Icon(SystemIcons.Application, size);
                using (Icon all = new Icon(s, size)) return (Icon)all.Clone();
            }
        }
    }

    // Native popup menus belong to the hidden owner HWND and never create a taskbar button.
    static class TrayMenu
    {
        [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu,uint flags,UIntPtr id,string text);
        [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr menu,uint flags,int x,int y,IntPtr owner,IntPtr parameters);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
        public static int Show(Form owner, params string[] items)
        {
            IntPtr menu=CreatePopupMenu();
            if(menu==IntPtr.Zero) return 0;
            try
            {
                for(int i=0;i<items.Length;i++) AppendMenu(menu,items[i]=="-"?0x800u:0u,new UIntPtr((uint)(i+1)),items[i]=="-"?null:items[i]);
                SetForegroundWindow(owner.Handle);
                Point p=Cursor.Position;
                int selected=TrackPopupMenuEx(menu,0x100u|0x2u|0x10u,p.X,p.Y,owner.Handle,IntPtr.Zero);
                PostMessage(owner.Handle,0,IntPtr.Zero,IntPtr.Zero);
                return selected;
            }
            finally { DestroyMenu(menu); }
        }
    }

    sealed class TrayFlyout : Form
    {
        [StructLayout(LayoutKind.Sequential)]struct NativePoint{public int X,Y;}
        [DllImport("user32.dll")]static extern bool PhysicalToLogicalPointForPerMonitorDPI(IntPtr hwnd,ref NativePoint point);
        string _text="";
        readonly Font _body=new Font("Microsoft YaHei UI",9.5f);
        readonly Font _title=new Font("Microsoft YaHei UI",10.5f,FontStyle.Bold);
        public TrayFlyout()
        {
            FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;
            StartPosition=FormStartPosition.Manual;BackColor=Color.FromArgb(22,31,46);
            DoubleBuffered=true;AutoScaleMode=AutoScaleMode.Dpi;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        { get { CreateParams p=base.CreateParams;p.ExStyle|=0x80|0x08000000;p.Style&=~0x800000;return p; } }
        public void SetText(string text)
        {
            _text=text??"";
            using(Graphics g=CreateGraphics())
            {
                float width=260,height=30;
                foreach(string line in _text.Replace("\r","").Split('\n'))
                {
                    width=Math.Max(width,g.MeasureString(line,line.StartsWith("  ")?_body:_title).Width);
                    height+=line.Length==0?9:_body.GetHeight(g)+4;
                }
                ClientSize=new Size(Math.Min(520,(int)Math.Ceiling(width)+42),Math.Min(650,(int)Math.Ceiling(height)));
            }
            Invalidate();
        }
        public void ShowNear(Point anchor)
        {
            ShowAtIcon(new Rectangle(anchor,new Size(1,1)));
        }
        public Rectangle ToLogical(Rectangle physical)
        {
            var first=new NativePoint{X=physical.Left,Y=physical.Top};var last=new NativePoint{X=physical.Right,Y=physical.Bottom};
            try{if(PhysicalToLogicalPointForPerMonitorDPI(Handle,ref first)&&PhysicalToLogicalPointForPerMonitorDPI(Handle,ref last))return Rectangle.FromLTRB(first.X,first.Y,last.X,last.Y);}catch(EntryPointNotFoundException){}
            return physical;
        }
        public void ShowAtIcon(Rectangle icon)
        {
            Rectangle area=Screen.FromRectangle(icon).WorkingArea;
            Location=TrayPlacement.NearIcon(area,icon,Size);
            Show();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.DrawRectangle(Pens.SlateGray,0,0,Width-1,Height-1);
            string[] lines=_text.Replace("\r","").Split('\n');float y=15;
            foreach(string line in lines)
            {
                bool heading=line.Length>0&&!line.StartsWith("  ");Font f=heading?_title:_body;
                using(Brush b=new SolidBrush(heading?Color.FromArgb(96,219,239):Color.FromArgb(222,230,240)))
                using(StringFormat format=new StringFormat()) { format.Trimming=StringTrimming.EllipsisCharacter;format.FormatFlags=StringFormatFlags.NoWrap;e.Graphics.DrawString(line,f,b,new RectangleF(18,y,Width-36,_title.GetHeight(e.Graphics)+4),format); }
                y+=line.Length==0?9:_body.GetHeight(e.Graphics)+4;
            }
        }
        protected override void Dispose(bool disposing) { if(disposing){_body.Dispose();_title.Dispose();}base.Dispose(disposing); }
    }
    public static class TrayPlacement
    {
        public static Point NearIcon(Rectangle area,Rectangle icon,Size card)
        {
            const int gap=10;
            int x=icon.Left+icon.Width/2-card.Width/2,y=icon.Top-card.Height-gap;
            // Prefer above a bottom tray; switch below for a top tray, or beside a vertical tray.
            if(icon.Left>=area.Right){x=icon.Left-card.Width-gap;y=icon.Top;}
            else if(icon.Right<=area.Left){x=icon.Right+gap;y=icon.Top;}
            else if(y<area.Top)y=icon.Bottom+gap;
            return new Point(Math.Max(area.Left,Math.Min(x,area.Right-card.Width)),Math.Max(area.Top,Math.Min(y,area.Bottom-card.Height)));
        }
    }
}
