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
        {return ShowChecked(owner,0,items);}
        public static int ShowChecked(Form owner,int checkedId,params string[] items)
        {
            IntPtr menu=CreatePopupMenu();
            if(menu==IntPtr.Zero) return 0;
            try
            {
                for(int i=0;i<items.Length;i++) AppendMenu(menu,items[i]=="-"?0x800u:i+1==checkedId?8u:0u,new UIntPtr((uint)(i+1)),items[i]=="-"?null:items[i]);
                SetForegroundWindow(owner.Handle);
                Point p=Cursor.Position;
                int selected=TrackPopupMenuEx(menu,0x100u|0x2u|0x10u,p.X,p.Y,owner.Handle,IntPtr.Zero);
                PostMessage(owner.Handle,0,IntPtr.Zero,IntPtr.Zero);
                return selected;
            }
            finally { DestroyMenu(menu); }
        }
    }

}
