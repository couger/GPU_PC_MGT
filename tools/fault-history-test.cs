using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using Gpm;
class Test
{
    static void Check(bool good,string text){if(!good)throw new Exception(text);Console.WriteLine("PASS "+text);}
    static Snapshot Good(double temp){return new Snapshot{Ok=true,HasGpu=true,GpuTempC=temp,SysTempC=65,CpuPct=15,GpuPct=95,MemPct=30,MemUsedBytes=20*1073741824.0,MemTotalBytes=64*1073741824.0,MemAvailBytes=44*1073741824.0};}
    static Snapshot Bad(){return new Snapshot{Ok=false,Error="测试连接超时（模拟设备不可达）"};}
    [STAThread]static void Main(string[] args)
    {
        DateTime now=DateTime.UtcNow;var target=new Target{Name="故障回放测试设备",Host="测试地址"};var recorder=new FaultRecorder();
        for(int i=-360;i<0;i+=2)recorder.Observe(target,Good(60+(i+360)/12.0),now.AddSeconds(i));
        var incident=recorder.Observe(target,Bad(),now);Check(incident!=null&&incident.Samples[0].ReceivedUtc==now.AddMinutes(-5)&&incident.Samples.Length==151,"freeze exactly preceding 5 minutes and failure boundary");
        recorder.Observe(target,Bad(),now.AddSeconds(2));Check(recorder.Records(now.AddSeconds(2)).Length==1,"continuous outage produces one record");
        int frozen=incident.Samples.Length;recorder.Observe(target,Good(70),now.AddSeconds(4));recorder.Observe(target,Bad(),now.AddSeconds(6));Check(recorder.Records(now.AddSeconds(6)).Length==2&&incident.Samples.Length==frozen,"recovery then failure creates independent immutable record");
        var other=new Target{Name="另一台设备"};Check(recorder.Observe(other,Bad(),now)==null,"startup failure with no prior measurements does not fabricate history");
        for(int i=0;i<20;i++){recorder.Observe(target,Good(70),now.AddMinutes(i+1));recorder.Observe(target,Bad(),now.AddMinutes(i+1).AddSeconds(1));}Check(recorder.Records(now.AddMinutes(22)).Length==10,"record count is bounded at ten");Check(recorder.Records(now.AddHours(25)).Length==0,"records expire after 24 hours");
        var rapid=new FaultRecorder();for(int i=0;i<4000;i++)rapid.Observe(target,Good(75),now.AddMilliseconds(i));Check(rapid.Observe(target,Bad(),now.AddSeconds(5)).Samples.Length<=3000,"fast sampling cannot grow buffer without bound");
        var shortBuffer=new FaultRecorder();shortBuffer.Observe(target,Good(60),now.AddSeconds(-2));Check(shortBuffer.Observe(target,Bad(),now).Samples.Length==2,"short startup retains available data without padding");
        var threads=new System.Threading.Thread[4];var parallel=new FaultRecorder();for(int n=0;n<4;n++){int key=n;threads[n]=new System.Threading.Thread(delegate(){var t=new Target{Name="线程"+key};for(int i=0;i<100;i++)parallel.Observe(t,Good(75),now.AddMilliseconds(i));parallel.Observe(t,Bad(),now.AddSeconds(1));});threads[n].Start();}foreach(var thread in threads)thread.Join();Check(parallel.Records(now.AddSeconds(1)).Length==4,"parallel target samplers record safely");
        for(int i=-300;i<0;i+=2)FaultHistory.Recorder.Observe(target,Good(65+(i+300)*0.08),now.AddSeconds(i));FaultHistory.Recorder.Observe(target,Bad(),now);
        if(Array.IndexOf(args,"--headless")>=0)return;
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Type type=typeof(Config).Assembly.GetType("Gpm.FaultHistoryForm");
        using(var form=(Form)Activator.CreateInstance(type,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance,null,new object[0],null))
        {
            form.Show();Application.DoEvents();var flags=BindingFlags.NonPublic|BindingFlags.Instance;var chart=(Chart)type.GetField("_chart",flags).GetValue(form);var grid=(DataGridView)type.GetField("_table",flags).GetValue(form);var metric=(ComboBox)type.GetField("_metric",flags).GetValue(form);
            Check(grid.Rows.Count==151&&grid.Columns.Count>=25,"table contains full readings with units");
            Check(chart.Series[0].Points.Count==150&&chart.Series[0].Points[149].YValues[0]>80,"failure is not rendered as zero temperature");
            using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(args[0],"fault-history-chart.png"),ImageFormat.Png);}
            for(int i=0;i<metric.Items.Count;i++){metric.SelectedIndex=i;Application.DoEvents();}Check(chart.Series.Count>0,"all chart categories render");
            var tabs=(TabControl)grid.Parent.Parent;tabs.SelectedIndex=1;Application.DoEvents();using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(args[0],"fault-history-table.png"),ImageFormat.Png);}form.Close();
        }
    }
}
