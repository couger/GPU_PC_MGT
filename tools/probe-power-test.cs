using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Gpm;
class ProbePowerTest
{
    static void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS "+text);}
    [STAThread] static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            Directory.CreateDirectory(args[0]);string path=Path.Combine(args[0],"probe-ui-test.json");
            new ProbePowerPreferences{Enabled=true}.Save(path);
            ProbePowerState state=new ProbePowerState{Effective=4,EnergySaver=0};
            using(var reminder=new ProbePowerReminder(path,delegate{return state;})){
                reminder.Tick(DateTime.UtcNow);Check(reminder.Notice==null,"best performance shows no warning");
                state=new ProbePowerState();reminder.Tick(DateTime.UtcNow);Check(reminder.Notice==null,"unknown API state does not invent a warning");
                state=new ProbePowerState{Effective=4,EnergySaver=1};reminder.Tick(DateTime.UtcNow);Application.DoEvents();
                Check(reminder.Notice!=null&&reminder.Notice.Visible,"modern energy saver overrides a performance plan");
                Check(reminder.Notice.TopMost&&!reminder.Notice.ShowInTaskbar,"visible reminder stays above windows without a taskbar button");
                using(var picture=new Bitmap(reminder.Notice.Width,reminder.Notice.Height)){reminder.Notice.DrawToBitmap(picture,new Rectangle(Point.Empty,picture.Size));picture.Save(Path.Combine(args[0],"probe-power-reminder.png"),ImageFormat.Png);}
                var first=reminder.Notice;first.Close();Application.DoEvents();reminder.Tick(DateTime.UtcNow.AddSeconds(30));Check(reminder.Notice==null,"closing reminder postpones for sixty seconds");
                reminder.Tick(DateTime.UtcNow.AddSeconds(61));Check(reminder.Notice!=null&&!ReferenceEquals(first,reminder.Notice),"nonperformance warning returns after postponement");
                state=new ProbePowerState{Effective=4,EnergySaver=0};reminder.Tick(DateTime.UtcNow);Check(reminder.Notice==null,"return to best performance automatically closes warning");
                state=new ProbePowerState{Effective=2,EnergySaver=0};reminder.Tick(DateTime.UtcNow);Check(reminder.Notice!=null,"balanced mode warns even with energy saver off");
                reminder.SetEnabled(false);Check(reminder.Notice==null&&!ProbePowerPreferences.Load(path).Enabled,"tray toggle closes warning and persists disabled preference");
                reminder.Tick(DateTime.UtcNow.AddHours(1));Check(reminder.Notice==null,"disabled feature never reopens reminder");
                reminder.SetEnabled(true);reminder.Tick(DateTime.UtcNow);Check(reminder.Notice!=null&&ProbePowerPreferences.Load(path).Enabled,"reenabling immediately resumes warnings");
            }
            using(var reader=new ProbePowerReader()){
                Thread.Sleep(1200);var actual=reader.Read();Check(actual.Known,"native callback supplies actual system power state without changes");Console.WriteLine("POWER effective="+actual.Effective+" energySaver="+actual.EnergySaver);
            }
            new ProbePowerPreferences{Enabled=false}.Save(path);using(var reloaded=new ProbePowerReminder(path,delegate{return state;})){reloaded.Tick(DateTime.UtcNow);Check(!reloaded.Enabled&&reloaded.Notice==null,"disabled preference survives process recreation");}
            return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
