using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
class TrayPolishTest
{
    static BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static void Check(bool condition,string description) { if(!condition)throw new Exception(description);Console.WriteLine("PASS "+description); }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Assembly a=Assembly.LoadFrom(Path.GetFullPath(args[0]));string output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
            Type config=a.GetType("Gpm.Config"),target=a.GetType("Gpm.Target"),snapshot=a.GetType("Gpm.Snapshot"),main=a.GetType("Gpm.MainForm");
            config.GetField("Dir").SetValue(null,output);config.GetField("DataDir").SetValue(null,output);
            object t=Activator.CreateInstance(target),s=Activator.CreateInstance(snapshot);
            target.GetField("Name").SetValue(t,"DEMO-WORKSTATION");target.GetField("Kind").SetValue(t,"local");target.GetField("Latest").SetValue(t,s);
            snapshot.GetField("Ok").SetValue(s,true);snapshot.GetField("HasGpu").SetValue(s,true);snapshot.GetField("CpuPct").SetValue(s,12.5);snapshot.GetField("MemPct").SetValue(s,34.2);snapshot.GetField("MemUsedBytes").SetValue(s,22.0*1073741824);snapshot.GetField("MemTotalBytes").SetValue(s,64.0*1073741824);snapshot.GetField("GpuPct").SetValue(s,56.7);snapshot.GetField("GpuTempC").SetValue(s,61.0);
            IList targets=(IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(target));targets.Add(t);
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using(Form form=(Form)Activator.CreateInstance(main,flags,null,new object[]{targets,Path.Combine(output,"test.json"),false},null))
            {
                ((Timer)main.GetField("_timer",flags).GetValue(form)).Stop();main.GetField("_trayHintShown",flags).SetValue(form,true);
                object tray=main.GetField("_tray",flags).GetValue(form);Type trayType=tray.GetType();
                try
                {
                    string text=(string)main.GetMethod("TrayText",flags).Invoke(form,null);
                    Check(text.Contains("\n")&&text.Contains("GB")&&text.Contains("°C")&&text.Length>63,"multiline metrics retain units and full content");
                    trayType.GetMethod("Update").Invoke(tray,new object[]{text});
                    Form card=(Form)trayType.GetField("_flyout",flags).GetValue(tray);
                    Check(!card.ShowInTaskbar,"hover card has no taskbar button");
                    CreateParams cp=(CreateParams)card.GetType().GetProperty("CreateParams",flags).GetValue(card,null);
                    Check((cp.ExStyle&0x80)!=0&&(cp.ExStyle&0x08000000)!=0,"hover card is a non-activating tool window");
                    using(Bitmap image=new Bitmap(card.Width,card.Height)){card.DrawToBitmap(image,new Rectangle(0,0,card.Width,card.Height));image.Save(Path.Combine(output,"tray-card-preview.png"));}
                    int clicked=0;trayType.GetField("Clicked",flags).SetValue(tray,new EventHandler(delegate{clicked++;}));trayType.GetField("_v4",flags).SetValue(tray,true);
                    Message notification=Message.Create(form.Handle,0x8001,IntPtr.Zero,new IntPtr((1<<16)|0x402));trayType.GetMethod("OnMessage").Invoke(tray,new object[]{notification});Check(clicked==0,"balloon appearance never restores main window");
                    notification=Message.Create(form.Handle,0x8001,IntPtr.Zero,new IntPtr((1<<16)|0x405));trayType.GetMethod("OnMessage").Invoke(tray,new object[]{notification});Check(clicked==1,"balloon click restores main window");
                    trayType.GetField("_v4",flags).SetValue(tray,false);notification=Message.Create(form.Handle,0x8001,new IntPtr(1),new IntPtr(0x202));trayType.GetMethod("OnMessage").Invoke(tray,new object[]{notification});Check(clicked==2,"legacy tray click still works");
                    snapshot.GetField("Ok").SetValue(s,false);string failed=(string)main.GetMethod("TrayText",flags).Invoke(form,null);Check(failed.Contains("连接异常")&&!failed.Contains("56.7"),"failed samples do not show stale GPU metrics");
                    Check(main.GetField("_trayMenu",flags)==null,"managed popup window removed in favor of native menu");
                    Check(a.GetManifestResourceStream("Gpm.monitor.ico")!=null&&a.GetManifestResourceStream("Gpm.monitor-warning.ico")!=null,"custom normal and warning icons embedded");
                    Check(form.Icon.Width>=32,"window uses custom high resolution icon");
                    main.GetProperty("CloseToTray",flags).SetValue(null,false,null);config.GetMethod("Save").Invoke(null,new object[]{Path.Combine(output,"test.json"),targets});main.GetProperty("CloseToTray",flags).SetValue(null,true,null);config.GetMethod("Load").Invoke(null,new object[]{Path.Combine(output,"test.json")});Check(!(bool)main.GetProperty("CloseToTray",flags).GetValue(null,null),"tray preference persists across reload");
                }
                finally { ((IDisposable)tray).Dispose(); }
            }
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex.GetBaseException().ToString());return 1;}
    }
}
