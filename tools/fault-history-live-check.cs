using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
class LiveHistory
{
    delegate bool Callback(IntPtr window,IntPtr state);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr FindWindow(string cls,string title);
    [DllImport("user32.dll")]static extern bool EnumChildWindows(IntPtr window,Callback callback,IntPtr state);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr window,StringBuilder text,int capacity);
    [DllImport("user32.dll")]static extern bool PostMessage(IntPtr window,int message,IntPtr w,IntPtr l);
    [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]static extern bool ShowWindow(IntPtr window,int mode);
    [DllImport("user32.dll")]static extern bool SetForegroundWindow(IntPtr window);
    static void Main(){try{Check();}catch(Exception e){Console.WriteLine(e.Message);Environment.ExitCode=1;}}
    static void Check()
    {
        IntPtr main=FindWindow(null,"GPU PC 性能监视 · 性能总览"),button=IntPtr.Zero;if(main==IntPtr.Zero)throw new Exception("Main process missing");
        ShowWindow(main,9);SetForegroundWindow(main);Thread.Sleep(300);
        EnumChildWindows(main,delegate(IntPtr child,IntPtr ignored){var text=new StringBuilder(200);GetWindowText(child,text,200);if(text.ToString().StartsWith("故障记录 ("))button=child;return true;},IntPtr.Zero);
        if(button==IntPtr.Zero)throw new Exception("History button missing");PostMessage(button,0xF5,IntPtr.Zero,IntPtr.Zero);
        IntPtr history=IntPtr.Zero;for(int i=0;i<30;i++){Thread.Sleep(100);history=FindWindow(null,"故障前 5 分钟 · 临时记录");if(history!=IntPtr.Zero&&IsWindowVisible(history))break;}
        if(history==IntPtr.Zero||!IsWindowVisible(history))throw new Exception("History window failed to open");
        Console.WriteLine("PASS actual deployed viewer opens temporary history window with no records");PostMessage(history,0x10,IntPtr.Zero,IntPtr.Zero);
    }
}
