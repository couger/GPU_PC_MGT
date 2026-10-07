using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
class TrayDesktopCheck
{
    [StructLayout(LayoutKind.Sequential)]struct Identifier{public int Size;public IntPtr Window;public int Id;public Guid Guid;}
    [StructLayout(LayoutKind.Sequential)]struct Rect{public int Left,Top,Right,Bottom;}
    delegate bool EnumProc(IntPtr window,IntPtr arg);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr FindWindow(string cls,string title);
    [DllImport("user32.dll")]static extern bool EnumWindows(EnumProc callback,IntPtr arg);
    [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr window,StringBuilder value,int count);
    [DllImport("user32.dll")]static extern IntPtr GetWindowLongPtr(IntPtr window,int index);
    [DllImport("user32.dll")]static extern bool GetWindowRect(IntPtr window,out Rect rect);
    [DllImport("user32.dll")]static extern bool PrintWindow(IntPtr window,IntPtr dc,uint flags);
    [DllImport("user32.dll")]static extern bool PostMessage(IntPtr window,int msg,IntPtr wp,IntPtr lp);
    [DllImport("user32.dll")]static extern bool SetProcessDPIAware();
    [DllImport("shell32.dll")]static extern int Shell_NotifyIconGetRect(ref Identifier id,out Rect rect);
    static StringBuilder _log=new StringBuilder();
    static void Write(string text){_log.AppendLine(text);Console.WriteLine(text);}
    [DllImport("user32.dll")]static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")]static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
    static List<IntPtr> Visible(uint target)
    {List<IntPtr> result=new List<IntPtr>();EnumWindows(delegate(IntPtr w,IntPtr ignored){uint pid;GetWindowThreadProcessId(w,out pid);if(pid==target&&IsWindowVisible(w))result.Add(w);return true;},IntPtr.Zero);return result;}
    static string Class(IntPtr w){StringBuilder s=new StringBuilder(256);GetClassName(w,s,256);return s.ToString();}
    static void Dump(List<IntPtr> windows,string stage)
    {foreach(IntPtr w in windows){long style=GetWindowLongPtr(w,-20).ToInt64();Write(stage+" HWND="+w+" class="+Class(w)+" exstyle=0x"+style.ToString("X"));if((style&0x40000)!=0||((style&0x80)==0&&Class(w)!="#32768"))throw new Exception(stage+" unexpected taskbar-eligible popup");}}
    static int Main(string[] args)
    {
        IntPtr owner=IntPtr.Zero;
        try
        {
            SetProcessDPIAware();
            Directory.CreateDirectory(args[1]);owner=FindWindow(null,args[0]);if(owner==IntPtr.Zero)throw new Exception("Owner window missing");
            uint pid;GetWindowThreadProcessId(owner,out pid);Write("PID="+pid+" SESSION="+Process.GetCurrentProcess().SessionId+" OWNER="+owner);
            if(args.Length>2&&args[2]=="monitor")
            {
                // Cancel menus on any window of this process before simulating a caption click.
                EnumWindows(delegate(IntPtr w,IntPtr ignored){uint windowPid;GetWindowThreadProcessId(w,out windowPid);if(windowPid==pid)PostMessage(w,0x1F,IntPtr.Zero,IntPtr.Zero);return true;},IntPtr.Zero);
                Thread.Sleep(250);PostMessage(owner,0x112,new IntPtr(0xF060),IntPtr.Zero);
                for(int i=0;i<30&&IsWindowVisible(owner);i++)Thread.Sleep(100);
                if(IsWindowVisible(owner))throw new Exception("Caption close did not hide window");
            }
            Identifier id=new Identifier{Size=Marshal.SizeOf(typeof(Identifier)),Window=owner,Id=1};Rect icon;int hr=Shell_NotifyIconGetRect(ref id,out icon);Write("Tray GetRect HRESULT="+hr);if(hr!=0)throw new Exception("Tray icon is absent");
            Write("ICON_RECT="+icon.Left+","+icon.Top+","+icon.Right+","+icon.Bottom);
            if(args.Length<3||args[2]!="shell-hover"){
                PostMessage(owner,0x8001,IntPtr.Zero,new IntPtr((1<<16)|0x7B));Thread.Sleep(400);List<IntPtr> menu=Visible(pid);Dump(menu,"MENU");if(menu.Find(delegate(IntPtr w){return Class(w)=="#32768";})==IntPtr.Zero)throw new Exception("Native menu did not open");
                PostMessage(owner,0x1F,IntPtr.Zero,IntPtr.Zero);Thread.Sleep(300);
            }
            int x=(icon.Left+icon.Right)/2,y=(icon.Top+icon.Bottom)/2;
            if(args.Length>=5){x=int.Parse(args[3]);y=int.Parse(args[4]);icon.Left=x-15;icon.Right=x+15;icon.Top=y-15;icon.Bottom=y+15;}
            // A hidden notification icon can report the overflow button's rectangle.
            // Open overflow when no visible overflow island contains that rectangle.
            bool overflowVisible=false;
            EnumWindows(delegate(IntPtr w,IntPtr ignored){if(IsWindowVisible(w)&&Class(w)=="TopLevelWindowForOverflowXamlIsland"){Rect bounds;GetWindowRect(w,out bounds);if(x>=bounds.Left&&x<bounds.Right&&y>=bounds.Top&&y<bounds.Bottom)overflowVisible=true;}return true;},IntPtr.Zero);
            if(!overflowVisible&&args.Length<5){SetCursorPos(x,y);mouse_event(2,0,0,0,UIntPtr.Zero);mouse_event(4,0,0,0,UIntPtr.Zero);Thread.Sleep(600);Shell_NotifyIconGetRect(ref id,out icon);x=(icon.Left+icon.Right)/2;y=(icon.Top+icon.Bottom)/2;Write("HOVER_ICON_RECT="+icon.Left+","+icon.Top+","+icon.Right+","+icon.Bottom);}
            SetCursorPos(icon.Left+2,icon.Top+2);Thread.Sleep(200);
            bool moved=SetCursorPos(x,y);mouse_event(1,1,0,0,UIntPtr.Zero);Point actual;GetCursorPos(out actual);Write("CURSOR requested="+x+","+y+" actual="+actual.X+","+actual.Y+" moved="+moved);Thread.Sleep(4000);List<IntPtr> hover=Visible(pid);hover.Remove(owner);Dump(hover,"HOVER");
            using(Bitmap screen=new Bitmap(800,500)){using(Graphics g=Graphics.FromImage(screen))g.CopyFromScreen(Math.Max(0,x-400),Math.Max(0,y-450),0,0,screen.Size);screen.Save(Path.Combine(args[1],"shell-hover-screen.png"),ImageFormat.Png);}
            if(hover.Find(delegate(IntPtr w){return Class(w)=="tooltips_class32";})!=IntPtr.Zero)throw new Exception("Application still owns a tooltip window");
            IntPtr card=IntPtr.Zero;
            EnumWindows(delegate(IntPtr w,IntPtr ignored){uint windowPid;GetWindowThreadProcessId(w,out windowPid);string cls=Class(w);if(windowPid!=pid&&IsWindowVisible(w)&&(cls=="tooltips_class32"||cls=="Xaml_WindowedPopupClass")){Rect bounds;GetWindowRect(w,out bounds);if(Math.Abs(bounds.Left-x)<600&&Math.Abs(bounds.Top-y)<400){card=w;Write("SHELL_TOOLTIP PID="+windowPid+" CLASS="+cls);}}return true;},IntPtr.Zero);
            if(card==IntPtr.Zero){EnumWindows(delegate(IntPtr w,IntPtr ignored){if(IsWindowVisible(w)){Rect bounds;GetWindowRect(w,out bounds);uint windowPid;GetWindowThreadProcessId(w,out windowPid);if(Math.Abs(bounds.Left-x)<600&&Math.Abs(bounds.Top-y)<400)Write("NEARBY pid="+windowPid+" class="+Class(w)+" rect="+bounds.Left+","+bounds.Top+","+bounds.Right+","+bounds.Bottom);}return true;},IntPtr.Zero);throw new Exception("Shell standard tooltip did not open");}
            Rect r;GetWindowRect(card,out r);
            Write("CARD_RECT="+r.Left+","+r.Top+","+r.Right+","+r.Bottom);
            int dx=Math.Max(0,Math.Max(r.Left-icon.Right,icon.Left-r.Right)),dy=Math.Max(0,Math.Max(r.Top-icon.Bottom,icon.Top-r.Bottom));
            if(dx>100||dy>100)throw new Exception("Hover card is not adjacent to its icon");
            Write("PASS Windows Shell owns the standard tooltip adjacent to the icon");
            try{using(Bitmap b=new Bitmap(r.Right-r.Left,r.Bottom-r.Top)){using(Graphics g=Graphics.FromImage(b)){IntPtr dc=g.GetHdc();bool printed;try{printed=PrintWindow(card,dc,0);}finally{g.ReleaseHdc(dc);}if(!printed)g.CopyFromScreen(r.Left,r.Top,0,0,b.Size);}b.Save(Path.Combine(args[1],"tray-hover-live.png"),ImageFormat.Png);}}catch(Exception ex){Write("Screen capture unavailable: "+ex.Message);}
            Write("PASS native menu and standard tooltip have no application taskbar button");
            PostMessage(owner,0x8001,IntPtr.Zero,new IntPtr((1<<16)|0x407));
            File.WriteAllText(Path.Combine(args[1],"tray-desktop-check.log"),_log.ToString());return 0;
        }
        catch(Exception ex){Write("FAIL "+ex.Message);try{File.WriteAllText(Path.Combine(args[1],"tray-desktop-check.log"),_log.ToString());}catch{}return 1;}
        finally {if(owner!=IntPtr.Zero){PostMessage(owner,0x1F,IntPtr.Zero,IntPtr.Zero);PostMessage(owner,0x8001,IntPtr.Zero,new IntPtr((1<<16)|0x407));if(args.Length>2&&args[2]=="monitor")PostMessage(owner,0x8001,IntPtr.Zero,new IntPtr((1<<16)|0x203));}}
    }
}
