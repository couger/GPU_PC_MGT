using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Gpm
{
    sealed class DashboardViewport:Panel
    {
        readonly DashboardPanel _content;int _offset;bool _drag;int _dragY,_dragOffset;
        public DashboardViewport(DashboardPanel content)
        {
            _content=content;DoubleBuffered=true;TabStop=true;BackColor=Color.FromArgb(16,23,34);
            Controls.Add(content);content.MouseWheel+=delegate(object sender,MouseEventArgs e){ScrollBy(-Math.Sign(e.Delta)*72);};
            content.MouseDown+=delegate{Focus();};Resize+=delegate{Arrange();};
        }
        public void Arrange(){if(_content==null)return;_content.Width=Math.Max(240,ClientSize.Width-14);_content.Height=_content.RequiredHeight(ClientSize.Height);ScrollBy(0);}
        public void ScrollBy(int amount){_offset=Math.Max(0,Math.Min(_offset+amount,Math.Max(0,_content.Height-Height)));_content.Location=new Point(0,-_offset);Invalidate();}
        Rectangle Thumb(){int h=Math.Max(40,(int)((double)Height*Height/_content.Height));return new Rectangle(Width-8,(int)((double)_offset/Math.Max(1,_content.Height-Height)*(Height-h)),4,h);}
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);if(_content==null||_content.Height<=Height)return;
            using(var track=new SolidBrush(Color.FromArgb(32,44,61)))e.Graphics.FillRectangle(track,Width-8,0,4,Height);
            using(var thumb=new SolidBrush(Color.FromArgb(89,113,139)))e.Graphics.FillRectangle(thumb,Thumb());
        }
        protected override void OnMouseWheel(MouseEventArgs e){base.OnMouseWheel(e);ScrollBy(-Math.Sign(e.Delta)*72);}
        protected override void OnKeyDown(KeyEventArgs e){base.OnKeyDown(e);if(e.KeyCode==Keys.PageDown||e.KeyCode==Keys.Down){ScrollBy(e.KeyCode==Keys.PageDown?Height-40:72);e.Handled=true;}if(e.KeyCode==Keys.PageUp||e.KeyCode==Keys.Up){ScrollBy(e.KeyCode==Keys.PageUp?-Height+40:-72);e.Handled=true;}}
        protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);Focus();if(e.X<Width-14)return;if(Thumb().Contains(e.Location)){_drag=true;_dragY=e.Y;_dragOffset=_offset;Capture=true;}else ScrollBy(e.Y<Thumb().Top?-Height+40:Height-40);}
        protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(_drag){int goal=_dragOffset+(int)((double)(e.Y-_dragY)*(_content.Height-Height)/Math.Max(1,Height-Thumb().Height));ScrollBy(goal-_offset);}}
        protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);_drag=false;Capture=false;}
    }
    // Presentation only: reads the existing snapshots and UI histories.
    sealed class DashboardPanel:TargetPanel
    {
        static readonly Color Background=Color.FromArgb(16,23,34),Surface=Color.FromArgb(25,35,49),Border=Color.FromArgb(43,57,74),Muted=Color.FromArgb(146,164,184),TextColor=Color.FromArgb(233,240,249);
        readonly Font _title=new Font("Microsoft YaHei UI",14,FontStyle.Bold),_label=new Font("Microsoft YaHei UI",9),_small=new Font("Microsoft YaHei UI",8),_value=new Font("Segoe UI",25,FontStyle.Bold),_section=new Font("Microsoft YaHei UI",10,FontStyle.Bold);
        public DashboardPanel(Target target):base(target){BackColor=Background;AccessibleName=target.Name+" 性能总览";AccessibleRole=AccessibleRole.Pane;}
        public int RequiredHeight(int viewport){return Math.Max(viewport,Width>=720?752:1448);}
        static GraphicsPath Shape(Rectangle r,int radius)
        {
            var path=new GraphicsPath();int d=radius*2;path.AddArc(r.Left,r.Top,d,d,180,90);path.AddArc(r.Right-d,r.Top,d,d,270,90);path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);path.AddArc(r.Left,r.Bottom-d,d,d,90,90);path.CloseFigure();return path;
        }
        void SurfaceAt(Graphics g,Rectangle r)
        {using(var path=Shape(r,12))using(var b=new SolidBrush(Surface))using(var p=new Pen(Border)){g.FillPath(b,path);g.DrawPath(p,path);}}
        void TextAt(Graphics g,string text,Font font,Color color,Rectangle r,bool right=false)
        {TextRenderer.DrawText(g,text??"",font,r,color,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis|(right?TextFormatFlags.Right:TextFormatFlags.Left));}
        static string Percent(double value){return Double.IsNaN(value)||Double.IsInfinity(value)?"—":value.ToString("0")+"%";}
        static string Bytes(double value){if(value>=1073741824)return(value/1073741824).ToString("0.0")+" GB";if(value>=1048576)return(value/1048576).ToString("0")+" MB";return(value/1024).ToString("0")+" KB";}
        static string Rate(double value){return value>=1048576?(value/1048576).ToString("0.0")+" MB/s":(value/1024).ToString("0")+" KB/s";}
        static string Reading(double value,string unit){return Double.IsNaN(value)||Double.IsInfinity(value)?"—":value.ToString("0")+unit;}
        void Bar(Graphics g,Rectangle r,double value,Color color)
        {using(var bg=new SolidBrush(Color.FromArgb(43,54,71)))g.FillRectangle(bg,r);if(Double.IsNaN(value))return;using(var b=new SolidBrush(color))g.FillRectangle(b,r.X,r.Y,(float)(r.Width*Math.Max(0,Math.Min(100,value))/100),r.Height);}
        void Trend(Graphics g,Rectangle r,List<double> first,Color color,List<double> second,Color secondColor,double maximum)
        {
            if(r.Width<10||r.Height<15)return;
            using(var p=new Pen(Color.FromArgb(39,53,71)))for(int i=0;i<3;i++){int y=r.Top+i*r.Height/2;g.DrawLine(p,r.Left,y,r.Right,y);}
            if(maximum<=0){maximum=1;foreach(var series in new List<double>[] {first,second})if(series!=null)foreach(double value in series)if(!Double.IsNaN(value))maximum=Math.Max(maximum,value);maximum*=1.2;}
            DrawSeries(g,r,first,color,maximum,true);DrawSeries(g,r,second,secondColor,maximum,false);
            if(first==null||first.Count<2)TextAt(g,"趋势将在采样后逐步显示",_small,Muted,new Rectangle(r.Left,r.Top+r.Height/2-8,r.Width,18));
        }
        static void DrawSeries(Graphics g,Rectangle r,List<double> series,Color color,double maximum,bool fill)
        {
            if(series==null||series.Count<2)return;
            var points=new PointF[series.Count];for(int i=0;i<points.Length;i++){double v=Double.IsNaN(series[i])?0:Math.Max(0,Math.Min(maximum,series[i]));points[i]=new PointF(r.Left+i*(float)r.Width/(points.Length-1),r.Bottom-1-(float)(v/maximum*(r.Height-2)));}
            if(fill){var area=new PointF[points.Length+2];Array.Copy(points,area,points.Length);area[points.Length]=new PointF(r.Right,r.Bottom);area[points.Length+1]=new PointF(r.Left,r.Bottom);using(var brush=new LinearGradientBrush(r,Color.FromArgb(48,color),Color.FromArgb(0,color),90))g.FillPolygon(brush,area);}
            using(var pen=new Pen(color,2))g.DrawLines(pen,points);
        }
        void Summary(Graphics g,Rectangle r,string label,string value,string note,Color color,List<double> history)
        {
            SurfaceAt(g,r);using(var b=new SolidBrush(color))g.FillRectangle(b,r.Left+17,r.Top+17,3,14);
            TextAt(g,label,_label,Muted,new Rectangle(r.Left+27,r.Top+13,r.Width-42,22));
            if(value!="—")DrawSeries(g,new Rectangle(r.Right-72,r.Top+16,55,18),history,color,100,false);
            TextAt(g,value,_value,TextColor,new Rectangle(r.Left+17,r.Top+36,r.Width-34,43));
            TextAt(g,note,_small,Muted,new Rectangle(r.Left+17,r.Bottom-24,r.Width-34,18));
        }
        void Heading(Graphics g,Rectangle r,string title,string note,Color color)
        {SurfaceAt(g,r);TextAt(g,title,_section,TextColor,new Rectangle(r.Left+18,r.Top+15,r.Width-110,23));TextAt(g,note,_small,color,new Rectangle(r.Right-115,r.Top+18,96,18),true);}
        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics;g.Clear(Background);g.SmoothingMode=SmoothingMode.AntiAlias;
            Snapshot s=T.Latest;bool ok=s!=null&&s.Ok;int pad=12,w=Width-24;if(w<100)return;
            AccessibleDescription=s==null?"等待设备采样":!s.Ok?"连接异常，暂无有效指标":("CPU "+Percent(s.CpuPct)+"，内存 "+Percent(s.MemPct)+"，GPU "+(s.HasGpu?Percent(s.GpuPct):"未检测到")+"，温度 "+Reading(s.GpuTempC,"摄氏度"));
            bool wide=Width>=720;int x=pad,y=14;
            TextAt(g,T.Name,_title,TextColor,new Rectangle(x,y,w-112,28));
            string status=s==null?"等待采样":ok?"连接正常":"连接异常";Color state=s==null?Muted:ok?Theme.Ok:Theme.Bad;
            using(var b=new SolidBrush(Color.FromArgb(30,state)))using(var path=Shape(new Rectangle(x+w-108,y,108,28),8))g.FillPath(b,path);
            TextAt(g,"●  "+status,_small,state,new Rectangle(x+w-97,y+5,98,20));
            string connection=T.Kind=="local"?"本机监控":T.Kind=="probe"?"远程监控 · "+T.Host:"远程监控 · SSH · "+T.Host;
            TextAt(g,connection,_small,Muted,new Rectangle(x,y+35,w-180,22));
            TextAt(g,s==null?"自动更新":s.At.ToString("HH:mm:ss")+" 更新 · "+s.CollectMs+" ms",_small,Muted,new Rectangle(x+w-190,y+35,190,22),true);
            y+=70;
            int gap=12,cols=wide?4:2,sw=(w-(cols-1)*gap)/cols;
            string[] labels={"处理器","内存","显卡","GPU 温度"};
            string[] values={ok?Percent(s.CpuPct):"—",ok?Percent(s.MemPct):"—",ok&&s.HasGpu?Percent(s.GpuPct):"—",ok?Reading(s.GpuTempC,"°C"):"—"};
            string[] notes={ok&&s.CpuMhz>0?(s.CpuMhz/1000).ToString("0.00")+" GHz · "+s.Cores.Count+" 个逻辑处理器":"CPU 使用率",ok?Bytes(s.MemUsedBytes)+" / "+Bytes(s.MemTotalBytes):"内存使用率",ok&&s.HasGpu?"显存占用 "+Bytes(s.VramUsed):"GPU 使用率",ok&&!Double.IsNaN(s.GpuTempC)?"温度传感器":"暂无传感器读数"};
            Color[] colors={Theme.Cpu,Theme.Mem,Theme.Gpu,Theme.Temp};
            var histories=new List<double>[] {T.HCpu,T.HMem,T.HGpu,T.HTemp};
            for(int i=0;i<4;i++)Summary(g,new Rectangle(x+(i%cols)*(sw+gap),y+(i/cols)*120,sw,108),labels[i],values[i],notes[i],colors[i],histories[i]);
            y+=wide?124:244;
            if(!ok)
            {
                Rectangle empty=new Rectangle(x,y,w,180);SurfaceAt(g,empty);
                TextAt(g,s==null?"正在连接设备":"暂时无法读取这台设备",_title,s==null?Theme.Cpu:Theme.Bad,new Rectangle(x+24,y+24,w-48,32));
                string message=s==null?"采样完成后，这里将显示处理器、显卡、内存和网络趋势。":"请检查设备连接，或打开连接设置。连接恢复后会自动更新。";
                TextAt(g,message,_label,Muted,new Rectangle(x+24,y+73,w-48,26));
                if(s!=null)TextRenderer.DrawText(g,s.Error,_small,new Rectangle(x+24,y+111,w-48,52),Muted,TextFormatFlags.WordBreak|TextFormatFlags.EndEllipsis);
                return;
            }
            int cw=wide?(w-gap)/2:w;
            int firstHeight=wide?Math.Max(260,(Height-y-44)/2):260,secondHeight=wide?Math.Max(240,Height-y-firstHeight-44):260;
            Rectangle cpu=new Rectangle(x,y,cw,firstHeight),gpu=new Rectangle(wide?x+cw+gap:x,wide?y:y+firstHeight+gap,cw,firstHeight);
            Cpu(g,cpu,s);Gpu(g,gpu,s);
            y+=wide?firstHeight+gap:firstHeight*2+gap*2;
            Rectangle memory=new Rectangle(x,y,cw,secondHeight),io=new Rectangle(wide?x+cw+gap:x,wide?y:y+secondHeight+gap,cw,secondHeight);
            Memory(g,memory,s);Io(g,io,s);
            int foot=wide?memory.Bottom+9:io.Bottom+9;
            TextAt(g,s.Os+(s.Uptime.Length>0?" · 已运行 "+s.Uptime:""),_small,Muted,new Rectangle(x,foot,w,22));
        }
        void Cpu(Graphics g,Rectangle r,Snapshot s)
        {
            Heading(g,r,"处理器趋势","使用率 · %",Theme.Cpu);TextAt(g,s.CpuName,_small,Muted,new Rectangle(r.Left+18,r.Top+43,r.Width-36,19));
            Trend(g,new Rectangle(r.Left+18,r.Top+76,r.Width-36,66),T.HCpu,Theme.Cpu,null,Theme.Cpu,100);
            TextAt(g,"逻辑处理器负载 · "+s.Cores.Count+" 个",_small,Muted,new Rectangle(r.Left+18,r.Top+151,r.Width-36,19));
            int cols=s.Cores.Count>16?8:Math.Max(4,Math.Min(8,s.Cores.Count)),rows=Math.Max(1,(s.Cores.Count+cols-1)/cols),cell=(r.Width-36-(cols-1)*4)/cols,ch=Math.Min(30,(r.Height-186-(rows-1)*3)/rows);
            for(int i=0;i<s.Cores.Count;i++){Rectangle box=new Rectangle(r.Left+18+(i%cols)*(cell+4),r.Top+176+(i/cols)*(ch+3),cell,ch);if(box.Bottom>r.Bottom-9)break;using(var b=new SolidBrush(Color.FromArgb(27+(int)(Math.Max(0,Math.Min(100,s.Cores[i]))*0.6),Theme.Cpu)))g.FillRectangle(b,box);TextAt(g,Percent(s.Cores[i]),_small,TextColor,new Rectangle(box.Left+2,box.Top+1,box.Width-4,ch));}
        }
        void Gpu(Graphics g,Rectangle r,Snapshot s)
        {
            Heading(g,r,"显卡趋势",s.HasGpu?"使用率 · %":"未检测到显卡",Theme.Gpu);TextAt(g,s.GpuName,_small,Muted,new Rectangle(r.Left+18,r.Top+43,r.Width-36,19));
            if(!s.HasGpu){TextAt(g,"设备未提供 GPU 指标",_label,Muted,new Rectangle(r.Left+18,r.Top+95,r.Width-36,30));return;}
            Trend(g,new Rectangle(r.Left+18,r.Top+76,r.Width-36,66),T.HGpu,Theme.Gpu,null,Theme.Gpu,100);
            var engines=new List<KeyValuePair<string,double>>(s.Engines);engines.Sort(delegate(KeyValuePair<string,double>a,KeyValuePair<string,double>b){int result=b.Value.CompareTo(a.Value);return result==0?String.CompareOrdinal(a.Key,b.Key):result;});
            int y=r.Top+152,index=0;foreach(var engine in engines){if(index++>=3)break;string name=EngineName(engine.Key);TextAt(g,name,_small,Muted,new Rectangle(r.Left+18,y,94,18));Bar(g,new Rectangle(r.Left+114,y+6,Math.Max(10,r.Width-181),4),engine.Value,Theme.Gpu);TextAt(g,Percent(engine.Value),_small,TextColor,new Rectangle(r.Right-62,y,44,18),true);y+=20;}
            TextAt(g,"显存 "+Bytes(s.VramUsed)+" · 共享 "+Bytes(s.VramShared),_small,Muted,new Rectangle(r.Left+18,r.Bottom-43,r.Width-36,18));
            string sensor="功耗 "+Reading(s.GpuPowerPct,"% TDP")+"  ·  风扇 "+Reading(s.GpuFanRpm," RPM")+"  ·  系统 "+Reading(s.SysTempC,"°C");TextAt(g,sensor,_small,Muted,new Rectangle(r.Left+18,r.Bottom-23,r.Width-36,18));
        }
        static string EngineName(string name)
        {
            switch(name){case "compute":return "计算";case "3d":return "3D";case "copy":return "数据复制";case "video jpeg":return "JPEG 解码";case "video processor":case "video processing":return "视频处理";case "video encode":return "视频编码";case "video decode":return "视频解码";case "video codec engine":case "video codec":return "视频编解码";case "security":return "安全处理";case "timer":return "计时";default:return name;}
        }
        void Memory(Graphics g,Rectangle r,Snapshot s)
        {
            Heading(g,r,"内存与存储","容量 · GB",Theme.Mem);
            TextAt(g,"内存 "+Bytes(s.MemUsedBytes)+" / "+Bytes(s.MemTotalBytes),_label,TextColor,new Rectangle(r.Left+18,r.Top+47,r.Width-36,24));
            Bar(g,new Rectangle(r.Left+18,r.Top+79,r.Width-36,6),s.MemPct,Theme.Mem);
            TextAt(g,"可用 "+Bytes(s.MemAvailBytes)+" · 已提交 "+Bytes(s.MemCommittedBytes),_small,Muted,new Rectangle(r.Left+18,r.Top+95,r.Width-36,22));
            int y=r.Top+130;foreach(var volume in s.Volumes)
            {
                if(y+43>r.Bottom-9)break;double used=volume.SizeBytes>0?(volume.SizeBytes-volume.FreeBytes)/volume.SizeBytes*100:0;
                TextAt(g,volume.Drive+"  "+(used>=92?"空间紧张":"磁盘容量"),_small,used>=92?Theme.Warn:TextColor,new Rectangle(r.Left+18,y,140,20));
                TextAt(g,Bytes(volume.FreeBytes)+" 可用 / "+Bytes(volume.SizeBytes),_small,Muted,new Rectangle(r.Left+158,y,r.Width-176,20),true);
                Bar(g,new Rectangle(r.Left+18,y+26,r.Width-36,5),used,used>=92?Theme.Warn:Theme.Disk);y+=45;
            }
            if(s.Volumes.Count==0)TextAt(g,"暂无磁盘容量信息",_small,Muted,new Rectangle(r.Left+18,y,r.Width-36,20));
        }
        void Io(Graphics g,Rectangle r,Snapshot s)
        {
            Heading(g,r,"网络与磁盘活动","实时速率",Theme.Cpu);
            int half=(r.Width-48)/2;
            TextAt(g,"↓ 接收",_small,Theme.Cpu,new Rectangle(r.Left+18,r.Top+48,half,18));TextAt(g,"↑ 发送",_small,Theme.Mem,new Rectangle(r.Left+30+half,r.Top+48,half,18));
            TextAt(g,Rate(s.NetRxBps),_section,TextColor,new Rectangle(r.Left+18,r.Top+69,half,28));TextAt(g,Rate(s.NetTxBps),_section,TextColor,new Rectangle(r.Left+30+half,r.Top+69,half,28));
            Trend(g,new Rectangle(r.Left+18,r.Top+104,r.Width-36,56),T.HNetR,Theme.Cpu,T.HNetT,Theme.Mem,0);
            TextAt(g,s.NetAdapter,_small,Muted,new Rectangle(r.Left+18,r.Top+169,r.Width-36,18));
            TextAt(g,"磁盘活动 "+Percent(s.DiskActive)+" · 队列 "+s.DiskQueue.ToString("0.00"),_small,TextColor,new Rectangle(r.Left+18,r.Bottom-43,r.Width-36,20));
            TextAt(g,"读取 "+Rate(s.DiskReadBps)+"   写入 "+Rate(s.DiskWriteBps),_small,Muted,new Rectangle(r.Left+18,r.Bottom-23,r.Width-36,20));
        }
        protected override void Dispose(bool disposing){if(disposing){_title.Dispose();_label.Dispose();_small.Dispose();_value.Dispose();_section.Dispose();}base.Dispose(disposing);}
    }
}
