using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Gpm;
class WidgetTest
{
    [DllImport("user32.dll")]static extern IntPtr WindowFromPoint(Point point);
    static void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS "+text);}
    static void Pump(int ms){DateTime end=DateTime.UtcNow.AddMilliseconds(ms);while(DateTime.UtcNow<end){Application.DoEvents();Thread.Sleep(20);}}
    [STAThread]static void Main(string[] args)
    {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        var area=Screen.PrimaryScreen.WorkingArea;Point previous=Cursor.Position;
        string path=Path.Combine(args[0],".tmp","widget-test.json");bool restored=false;
        var prefs=new WidgetPreferences{Edge="right",Enabled=true};
        var target=new Target{Name="DEMO-WORKSTATION",Latest=new Snapshot{Ok=true,CpuPct=24.5,MemPct=68.1,HasGpu=true,GpuPct=92,GpuTempC=76,NetRxBps=5242880,NetTxBps=307200}};
        var list=new List<Target>{target};Type type=typeof(Config).Assembly.GetType("Gpm.WidgetForm");
        using(var form=(Form)Activator.CreateInstance(type,new object[]{(Func<List<Target>>)(delegate{return list;}),(Action)(delegate{restored=true;}),prefs,path}))try
        {
            Cursor.Position=new Point(area.Left+area.Width/2,area.Top+area.Height/2);form.Show();Pump(1600);
            Check(!form.ShowInTaskbar&&form.TopMost,"tool widget hidden from taskbar and topmost");
            Check(form.Left==area.Right-WidgetGeometry.Tab,"right edge hides to visible tab");
            using(var graphics=form.CreateGraphics())Check(form.Region!=null&&form.Region.GetBounds(graphics).Width==WidgetGeometry.Tab,"hidden body clipped away from adjacent monitor");
            type.GetMethod("ShowAlert").Invoke(form,new object[]{target,"模拟高温提示"});Pump(700);
            Check(form.Left==area.Right-form.Width&&prefs.AutoHide,"alert slides out without changing auto-hide preference");
            using(var alertImage=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(alertImage,new Rectangle(Point.Empty,alertImage.Size));alertImage.Save(Path.Combine(args[0],".tmp","widget-alert-preview.png"),ImageFormat.Png);}
            type.GetField("_alertUntil",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(form,DateTime.UtcNow.AddSeconds(-1));Pump(900);
            Check(form.Left==area.Right-WidgetGeometry.Tab,"alert automatically retracts after expiry");
            Cursor.Position=new Point(area.Right-6,form.Top+80);Pump(700);Check(form.Left==area.Right-form.Width,"touching tab slides out");
            using(var other=new Form{FormBorderStyle=FormBorderStyle.None,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Bounds=form.Bounds})
            {
                other.Show();other.Activate();Pump(250);
                Check(WindowFromPoint(form.PointToScreen(new Point(150,50)))==form.Handle,"widget stays above an overlapping activated ordinary window");other.Close();
            }
            using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(args[0],".tmp","widget-preview.png"),ImageFormat.Png);}
            var down=type.GetMethod("OnMouseDown",BindingFlags.Instance|BindingFlags.NonPublic);
            down.Invoke(form,new object[]{new MouseEventArgs(MouseButtons.Left,1,225,15,0)});Check(form.TopMost&&prefs.TopMost,"pin click cannot accidentally disable topmost");
            type.GetMethod("SetPinned",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,new object[]{false});Check(!form.TopMost&&!prefs.TopMost,"explicit menu option can disable topmost");
            Cursor.Position=new Point(form.Left+50,form.Top+15);down.Invoke(form,new object[]{new MouseEventArgs(MouseButtons.Left,1,50,15,0)});
            Cursor.Position=new Point(area.Left+50,area.Top+115);type.GetMethod("OnMouseMove",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,new object[]{new MouseEventArgs(MouseButtons.Left,0,50,15,0)});
            type.GetMethod("OnMouseUp",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,new object[]{new MouseEventArgs(MouseButtons.Left,1,50,15,0)});
            Check(prefs.Edge=="left"&&form.Left==area.Left,"dragging snaps to left edge");
            Cursor.Position=new Point(area.Left+area.Width/2,area.Top+area.Height/2);Pump(1600);Check(form.Left==area.Left-form.Width+WidgetGeometry.Tab,"left edge hides to visible tab");
            Cursor.Position=new Point(area.Left+6,form.Top+80);Pump(700);Check(form.Left==area.Left,"left tab slides out");
            prefs.AutoHide=false;Cursor.Position=new Point(area.Left+area.Width/2,area.Top+area.Height/2);Pump(1000);Check(form.Left==area.Left,"auto hide can be disabled");
            down.Invoke(form,new object[]{new MouseEventArgs(MouseButtons.Left,1,250,15,0)});Check(restored,"full window restore action");
            prefs.Save(path);var saved=WidgetPreferences.Load(path);Check(saved.Edge=="left"&&!saved.TopMost&&!saved.AutoHide&&saved.Enabled,"preferences persist");
            var negative=new Rectangle(-1920,0,1920,1080);Check(WidgetGeometry.EdgeX(negative,300,"left",true)==-2208&&WidgetGeometry.EdgeX(negative,300,"right",true)==-12,"negative monitor coordinates");
            Check(WidgetGeometry.ClampY(area,160,Int32.MaxValue)==area.Bottom-160,"position stays inside work area");
        }
        finally{Cursor.Position=previous;}
    }
}
