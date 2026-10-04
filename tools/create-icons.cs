using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
class CreateIcons
{
    static GraphicsPath Round(float x,float y,float w,float h,float r)
    { GraphicsPath p=new GraphicsPath(); p.AddArc(x,y,r,r,180,90);p.AddArc(x+w-r,y,r,r,270,90);p.AddArc(x+w-r,y+h-r,r,r,0,90);p.AddArc(x,y+h-r,r,r,90,90);p.CloseFigure();return p; }
    static Bitmap Draw(int size,bool probe,bool warning)
    {
        Bitmap big=new Bitmap(size*4,size*4); using(Graphics g=Graphics.FromImage(big))
        {
            g.SmoothingMode=SmoothingMode.AntiAlias;g.ScaleTransform(size*4/256f,size*4/256f);
            Color accent=probe?Color.FromArgb(255,183,82):Color.FromArgb(71,209,235);
            using(GraphicsPath bg=Round(12,12,232,232,58)) using(LinearGradientBrush fill=new LinearGradientBrush(new Point(0,0),new Point(256,256),Color.FromArgb(34,54,78),Color.FromArgb(11,21,38))) g.FillPath(fill,bg);
            using(Pen pins=new Pen(Color.FromArgb(135,accent),9)) { pins.StartCap=pins.EndCap=LineCap.Round;foreach(int n in new int[]{88,128,168}) {g.DrawLine(pins,n,42,n,58);g.DrawLine(pins,n,198,n,214);g.DrawLine(pins,42,n,58,n);g.DrawLine(pins,198,n,214,n);} }
            using(GraphicsPath chip=Round(61,61,134,134,24)) using(Pen edge=new Pen(accent,11)) g.DrawPath(edge,chip);
            using(Pen pulse=new Pen(Color.FromArgb(244,249,255),12)) { pulse.LineJoin=LineJoin.Round;pulse.StartCap=pulse.EndCap=LineCap.Round;g.DrawLines(pulse,new PointF[]{new PointF(82,137),new PointF(107,137),new PointF(123,105),new PointF(141,153),new PointF(155,128),new PointF(175,128)}); }
            using(Brush rim=new SolidBrush(Color.FromArgb(12,23,39))) g.FillEllipse(rim,172,172,57,57);
            using(Brush dot=new SolidBrush(warning?Color.FromArgb(255,104,112):probe?Color.FromArgb(71,209,235):Color.FromArgb(90,228,163))) g.FillEllipse(dot,181,181,39,39);
        }
        Bitmap result=new Bitmap(size,size);using(Graphics g=Graphics.FromImage(result)){g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.DrawImage(big,0,0,size,size);}big.Dispose();return result;
    }
    static byte[] Dib(Bitmap b)
    {
        using(MemoryStream ms=new MemoryStream())using(BinaryWriter w=new BinaryWriter(ms))
        {
            int n=b.Width,mask=((n+31)/32)*4;w.Write(40);w.Write(n);w.Write(n*2);w.Write((short)1);w.Write((short)32);w.Write(0);w.Write(n*n*4+mask*n);w.Write(0);w.Write(0);w.Write(0);w.Write(0);
            for(int y=n-1;y>=0;y--)for(int x=0;x<n;x++){Color c=b.GetPixel(x,y);w.Write(c.B);w.Write(c.G);w.Write(c.R);w.Write(c.A);}
            for(int y=n-1;y>=0;y--){byte[] row=new byte[mask];for(int x=0;x<n;x++)if(b.GetPixel(x,y).A<128)row[x/8]|=(byte)(128>>(x%8));w.Write(row);}return ms.ToArray();
        }
    }
    static void Main(string[] args)
    {
        Directory.CreateDirectory(args[0]);foreach(string name in new string[]{"monitor","probe","monitor-warning","probe-warning"})
        {
            bool probe=name.StartsWith("probe"),warn=name.EndsWith("warning");int[] sizes={16,20,24,32,48,64,128,256};List<byte[]> entries=new List<byte[]>();
            foreach(int size in sizes)using(Bitmap b=Draw(size,probe,warn))
            {
                if(size==256)b.Save(Path.Combine(args[0],name+".png"),ImageFormat.Png);
                if(size>=128)using(MemoryStream png=new MemoryStream()){b.Save(png,ImageFormat.Png);entries.Add(png.ToArray());}
                else entries.Add(Dib(b));
            }
            using(BinaryWriter w=new BinaryWriter(File.Create(Path.Combine(args[0],name+".ico")))){w.Write((short)0);w.Write((short)1);w.Write((short)sizes.Length);int offset=6+16*sizes.Length;for(int i=0;i<sizes.Length;i++){w.Write((byte)(sizes[i]==256?0:sizes[i]));w.Write((byte)(sizes[i]==256?0:sizes[i]));w.Write((short)0);w.Write((short)1);w.Write((short)32);w.Write(entries[i].Length);w.Write(offset);offset+=entries[i].Length;}foreach(byte[] entry in entries)w.Write(entry);}
        }
    }
}
