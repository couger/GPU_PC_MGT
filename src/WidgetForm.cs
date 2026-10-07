using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Gpm
{
    public sealed class WidgetPreferences
    {
        public bool Enabled,TopMost=true,AutoHide=true;
        public string Edge="none",ScreenName="",TargetName="";
        public int X=Int32.MinValue,Y=80;
        public static WidgetPreferences Load(string path)
        {
            try{return new JavaScriptSerializer().Deserialize<WidgetPreferences>(File.ReadAllText(path))??new WidgetPreferences();}catch{return new WidgetPreferences();}
        }
        public void Save(string path)
        {
            try{File.WriteAllText(path,new JavaScriptSerializer().Serialize(this));}catch(Exception ex){UiLog.W("widget preferences: "+ex.Message);}
        }
    }
    // Pure geometry, shared by animation and desktop tests, including negative-coordinate monitors.
    public static class WidgetGeometry
    {
        public const int Tab=12;
        public static int EdgeX(Rectangle area,int width,string edge,bool hidden)
        {return edge=="left"?area.Left-(hidden?width-Tab:0):area.Right-(hidden?Tab:width);}
        public static int ClampY(Rectangle area,int height,int y){return Math.Max(area.Top,Math.Min(y,area.Bottom-height));}
        public static string Snap(Rectangle area,Rectangle window,int distance)
        {if(Math.Abs(window.Left-area.Left)<=distance)return "left";if(Math.Abs(window.Right-area.Right)<=distance)return "right";return "none";}
    }
    class WidgetForm:Form
    {
        readonly Func<List<Target>> _targets;
        readonly Action _restore;
        readonly WidgetPreferences _prefs;
        readonly string _path;
        readonly Timer _motion=new Timer();
        readonly Font _body=new Font("Microsoft YaHei UI",9),_number=new Font("Segoe UI",17,FontStyle.Bold),_small=new Font("Microsoft YaHei UI",8);
        Rectangle _area;
        bool _dragging,_menu;
        Point _dragCursor,_dragLocation;
        DateTime _outside=DateTime.UtcNow,_lastPaint=DateTime.MinValue;
        DateTime _alertUntil=DateTime.MinValue;
        string _alertText="";
        Target _alertTarget;
        public void ShowAlert(Target target,string message)
        {
            _alertTarget=target;_alertText=message;_alertUntil=DateTime.UtcNow.AddSeconds(8);Invalidate();
        }
        public WidgetForm(Func<List<Target>> targets,Action restore,WidgetPreferences prefs,string path)
        {
            _targets=targets;_restore=restore;_prefs=prefs;_path=path;
            Text="GPU PC 精简小部件";FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=prefs.TopMost;
            StartPosition=FormStartPosition.Manual;ClientSize=new Size(300,160);BackColor=Color.FromArgb(22,31,46);DoubleBuffered=true;AutoScaleMode=AutoScaleMode.None;
            Screen screen=Screen.PrimaryScreen;foreach(var item in Screen.AllScreens)if(item.DeviceName==prefs.ScreenName)screen=item;
            _area=screen.WorkingArea;
            if(prefs.Edge!="left"&&prefs.Edge!="right")prefs.Edge="none";
            int x=prefs.X==Int32.MinValue?_area.Right-Width-32:Math.Max(_area.Left,Math.Min(prefs.X,_area.Right-Width));
            if(prefs.Edge!="none")x=WidgetGeometry.EdgeX(_area,Width,prefs.Edge,false);
            Location=new Point(x,WidgetGeometry.ClampY(_area,Height,prefs.Y));
            _motion.Interval=20;_motion.Tick+=delegate{Animate();};_motion.Start();
        }
        protected override bool ShowWithoutActivation{get{return true;}}
        protected override void OnVisibleChanged(EventArgs e){base.OnVisibleChanged(e);if(Visible)TopMost=_prefs.TopMost;}
        protected override CreateParams CreateParams{get{var cp=base.CreateParams;cp.ExStyle|=0x08000080;return cp;}}
        protected override void WndProc(ref Message m){if(m.Msg==0x21){m.Result=new IntPtr(3);return;}base.WndProc(ref m);}
        public bool Docked {get{return _prefs.Edge!="none";}}
        void SavePosition()
        {
            _prefs.X=Docked?WidgetGeometry.EdgeX(_area,Width,_prefs.Edge,false):Left;
            _prefs.Y=Top;_prefs.ScreenName=Screen.FromRectangle(_area).DeviceName;_prefs.Save(_path);
            ClipToScreen();
        }
        void ClipToScreen()
        {
            Region previous=Region;
            if(!Docked||(Left>=_area.Left&&Right<=_area.Right))Region=null;
            else{int left=Math.Max(0,_area.Left-Left),right=Math.Min(Width,_area.Right-Left);Region=new Region(new Rectangle(left,0,Math.Max(0,right-left),Height));}
            if(previous!=null)previous.Dispose();
        }
        void Animate()
        {
            if(!Visible)return;
            if(DateTime.UtcNow-_lastPaint>TimeSpan.FromMilliseconds(500)){Invalidate();_lastPaint=DateTime.UtcNow;}
            if(_dragging||_menu||!Docked)return;
            bool inside=Bounds.Contains(Cursor.Position)&&_area.Contains(Cursor.Position);if(inside)_outside=DateTime.UtcNow;
            bool hidden=_prefs.AutoHide&&!inside&&DateTime.UtcNow-_outside>TimeSpan.FromMilliseconds(600)&&DateTime.UtcNow>=_alertUntil;
            int goal=WidgetGeometry.EdgeX(_area,Width,_prefs.Edge,hidden),delta=goal-Left;
            if(delta!=0){Left+=Math.Sign(delta)*Math.Min(Math.Abs(delta),Math.Max(1,(int)Math.Ceiling(Math.Abs(delta)*0.24)));ClipToScreen();}
            int top=WidgetGeometry.ClampY(_area,Height,Top);if(Top!=top)Top=top;
        }
        Target Selected()
        {
            if(DateTime.UtcNow<_alertUntil&&_alertTarget!=null)return _alertTarget;
            var list=_targets();foreach(var t in list)if(t.Name==_prefs.TargetName)return t;return list.Count>0?list[0]:null;
        }
        static string Value(double value,string unit){return Double.IsNaN(value)||Double.IsInfinity(value)?"—":value.ToString("0")+unit;}
        static string Rate(double value){return value>=1048576?(value/1048576).ToString("0.0")+"M/s":(value/1024).ToString("0")+"K/s";}
        void TextAt(Graphics g,string text,Font font,Color color,Rectangle rect)
        {TextRenderer.DrawText(g,text,font,rect,color,TextFormatFlags.NoPadding|TextFormatFlags.EndEllipsis|TextFormatFlags.SingleLine);}
        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics;g.Clear(BackColor);
            using(var border=new Pen(Color.FromArgb(62,83,108)))g.DrawRectangle(border,0,0,Width-1,Height-1);
            using(var strip=new SolidBrush(Theme.Cpu))g.FillRectangle(strip,_prefs.Edge=="left"?Width-4:0,0,4,Height);
            // The same visible strip is the hit target when the rest has slid off screen.
            if(Docked&&Math.Abs(Left-WidgetGeometry.EdgeX(_area,Width,_prefs.Edge,false))>Width/2)
            {
                int tabX=_prefs.Edge=="left"?Width-12:0;
                TextAt(g,"G",_small,Theme.Cpu,new Rectangle(tabX+1,65,11,18));
                TextAt(g,"P",_small,Theme.Cpu,new Rectangle(tabX+1,82,11,18));
            }
            Target target=Selected();Snapshot s=target==null?null:target.Latest;
            bool ok=s!=null&&s.Ok;
            TextAt(g,target==null?"没有启用的目标":target.Name,_body,Theme.Fg,new Rectangle(17,10,190,22));
            TextAt(g,_prefs.TopMost?"●":"○",_body,Theme.Cpu,new Rectangle(221,9,22,23));
            TextAt(g,"↗",_body,Theme.Fg,new Rectangle(247,9,22,23));
            TextAt(g,"≡",_body,Theme.Fg,new Rectangle(274,9,22,23));
            string[] labels={"CPU","内存","GPU"};Color[] colors={Theme.Cpu,Theme.Mem,Theme.Gpu};
            string[] values={ok?Value(s.CpuPct,"%" ):"—",ok?Value(s.MemPct,"%"):"—",ok&&s.HasGpu?Value(s.GpuPct,"%"):"—"};
            for(int i=0;i<3;i++)
            {
                int x=17+i*94;TextAt(g,labels[i],_small,colors[i],new Rectangle(x,43,90,18));TextAt(g,values[i],_number,Theme.Fg,new Rectangle(x,61,90,35));
                if(ok){double value=i==0?s.CpuPct:i==1?s.MemPct:s.HasGpu?s.GpuPct:0;using(var brush=new SolidBrush(colors[i]))g.FillRectangle(brush,x,99,(int)(80*Math.Min(100,Math.Max(0,value))/100),3);}
            }
            if(DateTime.UtcNow<_alertUntil)
            {
                TextRenderer.DrawText(g,"提醒 · "+_alertText,_small,new Rectangle(17,112,275,46),Theme.Warn,TextFormatFlags.WordBreak|TextFormatFlags.EndEllipsis);
                return;
            }
            TextAt(g,ok?"↓ "+Rate(s.NetRxBps)+"    ↑ "+Rate(s.NetTxBps):s==null?"正在等待采样…":"连接异常 · 暂无有效指标",_small,ok?Theme.Fg2:Theme.Bad,new Rectangle(17,114,203,20));
            TextAt(g,ok?Value(s.GpuTempC,"°C"):"—",_small,Theme.Temp,new Rectangle(228,114,65,20));
            TextAt(g,(s==null?"":s.At.ToString("HH:mm:ss")+"  ·  ")+(Docked?"贴边自动收起":"拖动标题贴边 · 右键设置"),_small,Theme.Dim,new Rectangle(17,138,274,18));
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if(e.Button==MouseButtons.Right){ShowWidgetMenu();return;}
            if(e.Button!=MouseButtons.Left)return;
            if(e.Y<35&&e.X>=274){ShowWidgetMenu();return;}
            if(e.Y<35&&e.X>=247){_restore();return;}
            if(e.Y<35&&e.X>=220){SetPinned(true);return;}
            // Dragging even the exposed tab reveals the widget and allows detaching it.
            if(Docked)Left=WidgetGeometry.EdgeX(_area,Width,_prefs.Edge,false);
            _prefs.Edge="none";_dragging=true;Capture=true;_dragCursor=Cursor.Position;_dragLocation=Location;
            ClipToScreen();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {base.OnMouseMove(e);if(_dragging){var point=Cursor.Position;Location=new Point(_dragLocation.X+point.X-_dragCursor.X,_dragLocation.Y+point.Y-_dragCursor.Y);}}
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);if(!_dragging)return;_dragging=false;Capture=false;_area=Screen.FromPoint(Cursor.Position).WorkingArea;
            _prefs.Edge=WidgetGeometry.Snap(_area,Bounds,24);
            if(Docked)Left=WidgetGeometry.EdgeX(_area,Width,_prefs.Edge,false);else Left=Math.Max(_area.Left,Math.Min(Left,_area.Right-Width));
            Top=WidgetGeometry.ClampY(_area,Height,Top);_outside=DateTime.UtcNow;SavePosition();Invalidate();
        }
        void ShowWidgetMenu()
        {
            _menu=true;
            try
            {
                var list=_targets();var items=new List<string>{"恢复完整窗口",(_prefs.TopMost?"✓ ":"")+"始终置顶",(_prefs.AutoHide?"✓ ":"")+"贴边自动收起","吸附到左侧","吸附到右侧","取消吸附","-"};
                foreach(var t in list)items.Add((Selected()==t?"✓ ":"")+t.Name);
                int choice=TrayMenu.Show(this,items.ToArray());
                if(choice==1){_restore();return;}
                if(choice==2)SetPinned(!_prefs.TopMost);
                if(choice==3)_prefs.AutoHide=!_prefs.AutoHide;
                if(choice==4||choice==5){_area=Screen.FromPoint(Cursor.Position).WorkingArea;_prefs.Edge=choice==4?"left":"right";Left=WidgetGeometry.EdgeX(_area,Width,_prefs.Edge,false);}
                if(choice==6){_prefs.Edge="none";Left=Math.Max(_area.Left,Math.Min(Left,_area.Right-Width));}
                if(choice>=8&&choice<8+list.Count)_prefs.TargetName=list[choice-8].Name;
                SavePosition();Invalidate();
            }
            finally{_menu=false;_outside=DateTime.UtcNow;}
        }
        void SetPinned(bool pinned){_prefs.TopMost=pinned;TopMost=pinned;SavePosition();Invalidate();}
        protected override void OnFormClosing(FormClosingEventArgs e){SavePosition();base.OnFormClosing(e);}
        protected override void Dispose(bool disposing){if(disposing){_motion.Dispose();_body.Dispose();_number.Dispose();_small.Dispose();}base.Dispose(disposing);}
    }
}
