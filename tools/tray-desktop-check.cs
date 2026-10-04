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
            PostMessage(owner,0x8001,IntPtr.Zero,new IntPtr((1<<16)|0x7B));Thread.Sleep(400);List<IntPtr> menu=Visible(pid);Dump(menu,"MENU");if(menu.Find(delegate(IntPtr w){return Class(w)=="#32768";})==IntPtr.Zero)throw new Exception("Native menu did not open");
            PostMessage(owner,0x1F,IntPtr.Zero,IntPtr.Zero);Thread.Sleep(300);
            int x=(icon.Left+icon.Right)/2,y=(icon.Top+icon.Bottom)/2;long xy=(ushort)x|((long)(ushort)y<<16);
            // Reproduce a zero anchor: the card must still follow the real icon.
            PostMessage(owner,0x8001,IntPtr.Zero,new IntPtr((1<<16)|0x406));Thread.Sleep(400);List<IntPtr> hover=Visible(pid);Dump(hover,"HOVER");
            IntPtr card=hover.Find(delegate(IntPtr w){return Class(w)!="#32768";});if(card==IntPtr.Zero)throw new Exception("Rich hover card did not open");
            Rect r;GetWindowRect(card,out r);
            Write("CARD_RECT="+r.Left+","+r.Top+","+r.Right+","+r.Bottom);
            int dx=Math.Max(0,Math.Max(r.Left-icon.Right,icon.Left-r.Right)),dy=Math.Max(0,Math.Max(r.Top-icon.Bottom,icon.Top-r.Bottom));
            if(dx>100||dy>100)throw new Exception("Hover card is not adjacent to its icon");
            Write("PASS hover follows icon even with zero callback coordinates");
            try{using(Bitmap b=new Bitmap(r.Right-r.Left,r.Bottom-r.Top)){using(Graphics g=Graphics.FromImage(b)){IntPtr dc=g.GetHdc();bool printed;try{printed=PrintWindow(card,dc,0);}finally{g.ReleaseHdc(dc);}if(!printed)g.CopyFromScreen(r.Left,r.Top,0,0,b.Size);}b.Save(Path.Combine(args[1],"tray-hover-live.png"),ImageFormat.Png);}}catch(Exception ex){Write("Screen capture unavailable: "+ex.Message);}
            Write("PASS native menu and rich hover have no taskbar button");
            PostMessage(owner,0x8001,IntPtr.Zero,new IntPtr((1<<16)|0x407));
            File.WriteAllText(Path.Combine(args[1],"tray-desktop-check.log"),_log.ToString());return 0;
        }
        catch(Exception ex){Write("FAIL "+ex.Message);try{File.WriteAllText(Path.Combine(args[1],"tray-desktop-check.log"),_log.ToString());}catch{}return 1;}
        finally {if(owner!=IntPtr.Zero){PostMessage(owner,0x1F,IntPtr.Zero,IntPtr.Zero);PostMessage(owner,0x8001,IntPtr.Zero,new IntPtr((1<<16)|0x407));if(args.Length>2&&args[2]=="monitor")PostMessage(owner,0x8001,IntPtr.Zero,new IntPtr((1<<16)|0x203));}}
    }
}
