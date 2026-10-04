using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using Gpm;
class Preview
{
    [STAThread]static void Main(string[] args)
    {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        string path=Path.Combine(args[0],"preview-config.json");var c=new ProbeConfig{Host="192.0.2.10",Allowed="192.0.2.20"};c.SetToken(ProbeConfig.NewToken());c.Save(path);
        using(var form=new ProbeSettingsForm(path)){form.Show();Application.DoEvents();using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(args[0],"probe-settings.png"),ImageFormat.Png);}form.Close();}
        Console.WriteLine("PASS probe settings rendered");
    }
}
