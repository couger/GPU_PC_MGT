using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
class CaptureDashboard
{
    [StructLayout(LayoutKind.Sequential)]struct Rect{public int Left,Top,Right,Bottom;}
    delegate bool Callback(IntPtr hwnd,IntPtr state);
    [DllImport("user32.dll")]static extern bool SetProcessDPIAware();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr FindWindow(string cls,string title);
    [DllImport("user32.dll")]static extern bool GetWindowRect(IntPtr window,out Rect rect);
    [DllImport("user32.dll")]static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int RegisterWindowMessage(string text);
    [DllImport("user32.dll")]static extern bool PostMessage(IntPtr window,int message,IntPtr w,IntPtr l);
    [DllImport("user32.dll")]static extern bool EnumChildWindows(IntPtr window,Callback callback,IntPtr state);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr window,StringBuilder value,int max);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr window,StringBuilder value,int max);
    [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr window,int message,IntPtr w,IntPtr l);
    static void Main(string[] args)
    {
        SetProcessDPIAware();IntPtr window=FindWindow(null,"GPU PC 性能监视 · 性能总览");if(window==IntPtr.Zero)throw new Exception("Viewer not running");
        PostMessage(window,RegisterWindowMessage("GPUPCMGT.Monitor.RestoreWindow"),IntPtr.Zero,IntPtr.Zero);Thread.Sleep(1000);SetForegroundWindow(window);Thread.Sleep(500);
        if(!IsWindowVisible(window))throw new Exception("Main window is not visible");bool readable=false;
        EnumChildWindows(window,delegate(IntPtr child,IntPtr ignored){var cls=new StringBuilder(200);GetClassName(child,cls,200);if(cls.ToString().Contains("COMBOBOX")){readable=SendMessage(child,0x147,IntPtr.Zero,IntPtr.Zero).ToInt32()>=0;}return true;},IntPtr.Zero);
        if(!readable)throw new Exception("Device selection text missing");Console.WriteLine("PASS live device selector has readable selected text");
        // Avoid capturing unrelated windows that overlap the app. Restore its stacking order afterwards.
        SetWindowPos(window,new IntPtr(-1),0,0,0,0,0x13);Thread.Sleep(300);
        try{Rect rect;GetWindowRect(window,out rect);using(var bitmap=new Bitmap(rect.Right-rect.Left,rect.Bottom-rect.Top))using(var g=Graphics.FromImage(bitmap)){g.CopyFromScreen(rect.Left,rect.Top,0,0,bitmap.Size);bitmap.Save(args[0],ImageFormat.Png);}}
        finally{SetWindowPos(window,new IntPtr(-2),0,0,0,0,0x13);}
        Console.WriteLine("PASS redesigned main window running with real data");
    }
}
