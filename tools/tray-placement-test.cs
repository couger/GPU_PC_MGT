using System;
using System.Drawing;
using Gpm;
class Test
{
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);}
    static void Main()
    {
        var size=new Size(300,200);var area=new Rectangle(0,0,1920,1040);
        var bottom=TrayPlacement.NearIcon(area,new Rectangle(1820,1050,24,24),size);Check(bottom.X==1620&&bottom.Y==840,"bottom tray anchored above icon within screen");
        var top=TrayPlacement.NearIcon(new Rectangle(0,40,1920,1040),new Rectangle(1810,8,24,24),size);Check(top.Y==42,"top tray anchored below icon");
        var left=TrayPlacement.NearIcon(new Rectangle(40,0,1880,1080),new Rectangle(8,700,24,24),size);Check(left.X==42,"left tray anchored beside icon");
        var right=TrayPlacement.NearIcon(new Rectangle(0,0,1880,1080),new Rectangle(1888,700,24,24),size);Check(right.X==1578,"right tray anchored beside icon");
        var negative=TrayPlacement.NearIcon(new Rectangle(-1920,-1080,1920,1040),new Rectangle(-100,-30,24,24),size);Check(negative.X==-300&&negative.Y==-240,"negative monitor coordinates stay near icon");
    }
}
