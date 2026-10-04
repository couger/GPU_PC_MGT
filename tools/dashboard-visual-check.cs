using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Gpm;
class DashboardCheck
{
    static BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);}
    static Target TargetData(string name)
    {
        var t=new Target{Name=name,Kind="probe",Host="192.0.2.10",Latest=new Snapshot{Ok=true,CpuPct=24,MemPct=38,GpuPct=92,HasGpu=true,GpuTempC=78,SysTempC=63,GpuFanRpm=1650,GpuPowerPct=48,CpuMhz=3200,CpuName="AMD Ryzen AI MAX+ 395 · 16 核处理器",GpuName="AMD Radeon 8060S",MemUsedBytes=24*1073741824.0,MemTotalBytes=64*1073741824.0,MemAvailBytes=40*1073741824.0,MemCommittedBytes=28*1073741824.0,VramUsed=11*1073741824.0,VramShared=512*1048576.0,NetRxBps=5.2*1048576,NetTxBps=360*1024,NetAdapter="MediaTek Wi-Fi 7 · 无线网络",DiskActive=12,DiskReadBps=18*1048576,DiskWriteBps=2.4*1048576,Os="Windows 11 专业版",Uptime="2天 06:18",CollectMs=38}};
        for(int i=0;i<32;i++)t.Latest.Cores.Add((i*7)%52);
        t.Latest.Engines.Add("compute",92);t.Latest.Engines.Add("3d",18);t.Latest.Engines.Add("copy",6);
        t.Latest.Volumes.Add(new Volume{Drive="C:",SizeBytes=1000*1073741824.0,FreeBytes=38*1073741824.0});t.Latest.Volumes.Add(new Volume{Drive="D:",SizeBytes=2000*1073741824.0,FreeBytes=1300*1073741824.0});
        for(int i=0;i<120;i++){t.HCpu.Add(23+Math.Sin(i*0.17)*14);t.HMem.Add(34+i*0.03);t.HGpu.Add(58+Math.Sin(i*0.09)*33);t.HTemp.Add(68+Math.Sin(i*0.07)*10);t.HNetR.Add((3+Math.Sin(i*0.12)*2)*1048576);t.HNetT.Add((1+Math.Cos(i*0.18))*262144);}
        return t;
    }
    static void Capture(Control control,string path){using(var image=new Bitmap(control.Width,control.Height)){control.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(path,ImageFormat.Png);}}
    [STAThread]static void Main(string[] args)
    {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        string output=args[0];Directory.CreateDirectory(output);Config.Dir=output;Config.DataDir=output;
        Assembly assembly=typeof(Config).Assembly;Type mainType=assembly.GetType("Gpm.MainForm"),panelType=assembly.GetType("Gpm.DashboardPanel");
        var target=TargetData("DEMO-WORKSTATION · 图形工作站");
        using(var panel=(Control)Activator.CreateInstance(panelType,flags,null,new object[]{target},null))
        {
            panel.Size=new Size(1280,800);Capture(panel,Path.Combine(output,"dashboard-full.png"));Check(panel.AccessibleDescription.Contains("CPU 24%")&&panel.AccessibleDescription.Contains("GPU 92%"),"key metrics have readable accessibility labels");
            panel.Size=new Size(520,1448);Capture(panel,Path.Combine(output,"dashboard-narrow.png"));
            target.Latest.Ok=false;Capture(panel,Path.Combine(output,"dashboard-disconnected.png"));Check(panel.AccessibleDescription.Contains("连接异常")&&!panel.AccessibleDescription.Contains("92%"),"connection failure removes stale readings");
            target.Latest=null;Capture(panel,Path.Combine(output,"dashboard-waiting.png"));Check(panel.AccessibleDescription.Contains("等待"),"waiting state is explicit");
        }
        target=TargetData("DEMO-WORKSTATION · 图形工作站");var second=TargetData("本机 · 名称很长的设备，用来检查窄窗口是否重叠");second.Kind="local";
        using(var form=(Form)Activator.CreateInstance(mainType,flags,null,new object[]{new List<Target>{target,second},Path.Combine(output,"test-config.json"),true},null))
        {
            mainType.GetField("RestoreWidgetOnShow").SetValue(form,false);form.Show();Application.DoEvents();
            var panels=(IList)mainType.GetField("_panels",flags).GetValue(form);Check(panels.Count==2,"multiple enabled devices retain separate views");
            Capture(form,Path.Combine(output,"dashboard-two-devices.png"));
            form.Size=new Size(940,650);Application.DoEvents();Capture(form,Path.Combine(output,"dashboard-small-window.png"));
            foreach(Control panel in panels)
            {
                Control viewport=panel.Parent;Check(panel.Height>viewport.Height&&panel.Width<=viewport.Width,"narrow device view remains scrollable without horizontal clipping");
                typeof(Control).GetMethod("OnMouseWheel",flags).Invoke(panel,new object[]{new MouseEventArgs(MouseButtons.None,0,40,40,-120)});Check(panel.Top<0,"mouse wheel reaches additional dashboard details");
                viewport.GetType().GetMethod("ScrollBy").Invoke(viewport,new object[]{Int32.MaxValue/2});Check(panel.Bottom<=viewport.Height,"scroll reaches final details");
            }
            mainType.GetMethod("ExitApplication").Invoke(form,null);
        }
    }
}
