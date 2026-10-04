using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
class Live
{
    [StructLayout(LayoutKind.Sequential)]struct Rect{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")]static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")]static extern bool GetWindowRect(IntPtr hwnd,out Rect rect);
    delegate bool Callback(IntPtr hwnd,IntPtr state);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr FindWindow(string cls,string title);
    [DllImport("user32.dll")]static extern bool EnumChildWindows(IntPtr parent,Callback cb,IntPtr state);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr hwnd,StringBuilder text,int capacity);
    [DllImport("user32.dll")]static extern bool PostMessage(IntPtr hwnd,int msg,IntPtr w,IntPtr l);
    [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]static extern IntPtr GetWindowLongPtr(IntPtr hwnd,int index);
    [DllImport("user32.dll")]static extern bool PrintWindow(IntPtr hwnd,IntPtr dc,uint flags);
    static void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS "+text);}
    static void Main(string[] args)
    {
        SetProcessDPIAware();
        IntPtr main=FindWindow(null,"GPU PC 性能监视 · 性能总览"),button=IntPtr.Zero;
        Check(main!=IntPtr.Zero,"viewer running");
        EnumChildWindows(main,delegate(IntPtr hwnd,IntPtr state){var text=new StringBuilder(100);GetWindowText(hwnd,text,100);if(text.ToString()=="小部件")button=hwnd;return true;},IntPtr.Zero);
        Check(button!=IntPtr.Zero,"widget entry available");PostMessage(button,0xF5,IntPtr.Zero,IntPtr.Zero);Thread.Sleep(2500);
        IntPtr widget=FindWindow(null,"GPU PC 精简小部件");Check(widget!=IntPtr.Zero&&IsWindowVisible(widget)&&!IsWindowVisible(main),"viewer switches to live widget");
        long style=GetWindowLongPtr(widget,-20).ToInt64();Check((style&0x80)!=0&&(style&0x40000)==0,"live widget retains tool window behavior");
        Rect rect;GetWindowRect(widget,out rect);
        using(var bitmap=new Bitmap(rect.Right-rect.Left,rect.Bottom-rect.Top))using(var g=Graphics.FromImage(bitmap)){IntPtr dc=g.GetHdc();try{PrintWindow(widget,dc,0);}finally{g.ReleaseHdc(dc);}bitmap.Save(Path.Combine(args[0],"data","widget-live.png"),ImageFormat.Png);}
        int clickX=(rect.Right-rect.Left)*250/300,clickY=(rect.Bottom-rect.Top)*15/160;
        PostMessage(widget,0x201,new IntPtr(1),new IntPtr(clickX|(clickY<<16)));Thread.Sleep(700);
        Check(IsWindowVisible(main)&&!IsWindowVisible(widget),"widget restores full viewer");
        PostMessage(button,0xF5,IntPtr.Zero,IntPtr.Zero);Thread.Sleep(700);Check(IsWindowVisible(widget)&&!IsWindowVisible(main),"widget can be reopened");
    }
}
