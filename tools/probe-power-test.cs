using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Gpm;
class ProbePowerTest
{
    sealed class FakeModes:IProbePowerModes
    {
        public ProbePowerMode AC=ProbePowerMode.BestEfficiency,DC=ProbePowerMode.Balanced;public int Writes;public bool Fail;
        public ProbePowerMode Get(bool plugged){return plugged?AC:DC;}
        public void Set(bool plugged,ProbePowerMode mode){if(Fail)throw new InvalidOperationException("simulated policy rejection");Writes++;if(plugged)AC=mode;else DC=mode;}
    }
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
            string configPath=Path.Combine(args[0],"power-settings-test.json");var cfg=new ProbeConfig();cfg.SetToken(ProbeConfig.NewToken());
            File.WriteAllText(configPath,new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(cfg));var fake=new FakeModes();
            using(var settings=new ProbeSettingsForm(configPath,fake,delegate{return new ProbePowerState{EnergySaver=1};})){
                settings.Show();Application.DoEvents();settings.RefreshPowerModes();Check(fake.Writes==0,"opening or refreshing settings never changes power mode");
                ComboBox ac=null,dc=null;foreach(Control c in settings.Controls)if(c is ComboBox){if(ac==null)ac=(ComboBox)c;else dc=(ComboBox)c;}
                Check(ac!=null&&dc!=null&&ac.Items.Count==3&&ac.Items[0].ToString()=="最佳能效"&&ac.Items[1].ToString()=="平衡"&&ac.Items[2].ToString()=="最佳性能","settings expose exactly the three Windows power mode names");
                settings.ApplyPowerMode(true,ProbePowerMode.BestPerformance);Check(fake.AC==ProbePowerMode.BestPerformance&&fake.DC==ProbePowerMode.Balanced&&ac.SelectedIndex==2,"AC selection applies immediately without changing battery mode");
                settings.ApplyPowerMode(false,ProbePowerMode.BestEfficiency);Check(fake.DC==ProbePowerMode.BestEfficiency&&fake.AC==ProbePowerMode.BestPerformance,"battery selection leaves AC choice unchanged");
                fake.AC=ProbePowerMode.Balanced;settings.RefreshPowerModes();Check(ac.SelectedIndex==1,"external system changes are reflected in settings");
                fake.Fail=true;settings.ApplyPowerMode(true,ProbePowerMode.BestPerformance);Check(ac.SelectedIndex==1,"rejected switch retains actual mode rather than claiming success");
                using(var picture=new Bitmap(settings.Width,settings.Height)){settings.DrawToBitmap(picture,new Rectangle(Point.Empty,picture.Size));picture.Save(Path.Combine(args[0],"probe-power-settings.png"),ImageFormat.Png);}
            }
            var nativeModes=new WindowsProbePowerModes();Check(nativeModes.Get(true)!=ProbePowerMode.Unknown&&nativeModes.Get(false)!=ProbePowerMode.Unknown,"documented Windows API reads AC and DC selections without changes");
            return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
