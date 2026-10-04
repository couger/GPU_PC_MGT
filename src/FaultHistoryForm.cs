using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

namespace Gpm
{
    sealed class FaultHistoryForm:Form
    {
        readonly ListBox _records=new ListBox();readonly Label _info=new Label();readonly ComboBox _metric=new ComboBox();
        readonly Chart _chart=new Chart();readonly DataGridView _table=new DataGridView();readonly Timer _refresh=new Timer();
        readonly Font _font=new Font("Microsoft YaHei UI",9);
        FaultRecord _selected;
        static readonly Color Bg=Color.FromArgb(20,29,42),Fg=Color.FromArgb(230,239,249);
        public FaultHistoryForm()
        {
            Text="故障前 5 分钟 · 临时记录";ClientSize=new Size(1180,740);MinimumSize=new Size(900,580);StartPosition=FormStartPosition.CenterScreen;BackColor=Bg;ForeColor=Fg;Font=_font;Icon=AppIcons.Load("monitor",new Size(32,32));
            var hint=new Label{Dock=DockStyle.Top,Height=52,Padding=new Padding(16,12,8,0),Text="仅内存保留：24 小时 / 最多 10 条；退出应用即清除。连接异常不代表已确认重启或过热。",ForeColor=Color.LightSkyBlue};Controls.Add(hint);
            var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Padding=new Padding(12)};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,240));layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            _records.Dock=DockStyle.Fill;_records.BackColor=Bg;_records.ForeColor=Fg;_records.BorderStyle=BorderStyle.FixedSingle;_records.SelectedIndexChanged+=delegate{_selected=_records.SelectedItem as FaultRecord;Render();};layout.Controls.Add(_records,0,0);
            var right=new Panel{Dock=DockStyle.Fill};_info.Dock=DockStyle.Top;_info.Height=66;_info.Padding=new Padding(12,6,4,4);right.Controls.Add(_info);
            var tabs=new TabControl{Dock=DockStyle.Fill};var graph=new TabPage("折线图"){BackColor=Bg};var table=new TabPage("采样表格"){BackColor=Bg};tabs.TabPages.Add(graph);tabs.TabPages.Add(table);right.Controls.Add(tabs);tabs.BringToFront();
            _metric.Dock=DockStyle.Top;_metric.DropDownStyle=ComboBoxStyle.DropDownList;_metric.Items.AddRange(new object[]{"温度（°C）","使用率（%）","内存（GB）","显存（GB）","网络收发（MB/s）","磁盘读写（MB/s）","CPU 频率（MHz）","风扇（RPM）","GPU 功耗（% TDP）","磁盘队列"});_metric.SelectedIndex=0;_metric.SelectedIndexChanged+=delegate{RenderChart();};graph.Controls.Add(_metric);
            _chart.Dock=DockStyle.Fill;_chart.BackColor=Bg;_chart.ChartAreas.Add(new ChartArea("metrics"));_chart.Legends.Add(new Legend{BackColor=Bg,ForeColor=Fg,Docking=Docking.Bottom});_chart.GetToolTipText+=delegate(object sender,ToolTipEventArgs e){if(e.HitTestResult.Series!=null&&e.HitTestResult.PointIndex>=0){var point=e.HitTestResult.Series.Points[e.HitTestResult.PointIndex];e.Text=DateTime.FromOADate(point.XValue).ToString("HH:mm:ss")+" · "+e.HitTestResult.Series.Name+" "+(point.IsEmpty?"无读数":point.YValues[0].ToString("0.00"));}};graph.Controls.Add(_chart);_chart.BringToFront();
            _table.Dock=DockStyle.Fill;_table.ReadOnly=true;_table.AllowUserToAddRows=false;_table.AllowUserToDeleteRows=false;_table.RowHeadersVisible=false;_table.BackgroundColor=Bg;_table.BorderStyle=BorderStyle.None;_table.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.DisplayedCells;_table.DefaultCellStyle.BackColor=Bg;_table.DefaultCellStyle.ForeColor=Fg;_table.AlternatingRowsDefaultCellStyle.BackColor=Color.FromArgb(29,41,56);_table.EnableHeadersVisualStyles=false;_table.ColumnHeadersDefaultCellStyle.BackColor=Color.FromArgb(40,56,75);_table.ColumnHeadersDefaultCellStyle.ForeColor=Fg;_table.SelectionMode=DataGridViewSelectionMode.FullRowSelect;table.Controls.Add(_table);
            layout.Controls.Add(right,1,0);Controls.Add(layout);layout.BringToFront();Reload();_refresh.Interval=2000;_refresh.Tick+=delegate{Reload();};_refresh.Start();
        }
        void Reload()
        {
            var records=FaultHistory.Records();bool same=_records.Items.Count==records.Length;for(int i=0;same&&i<records.Length;i++)same=ReferenceEquals(_records.Items[i],records[i]);if(same){if(records.Length==0)Render();return;}
            var selected=_selected;_records.BeginUpdate();_records.Items.Clear();foreach(var record in records)_records.Items.Add(record);_records.EndUpdate();
            int index=selected==null?-1:_records.Items.IndexOf(selected);if(_records.Items.Count>0)_records.SelectedIndex=index>=0?index:0;else{_selected=null;Render();}
        }
        void Render()
        {
            _table.Rows.Clear();_table.Columns.Clear();
            if(_selected==null){_info.Text="尚无故障记录。正常采样仅缓存最近 5 分钟，首次连接异常时自动冻结。";RenderChart();return;}
            var r=_selected;_info.Text=r.Name+" · "+r.Host+" · "+r.FaultUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")+" 连接异常\n"+r.Samples.Length+" 次采样 · "+(r.FaultUtc-r.Samples[0].ReceivedUtc).TotalSeconds.ToString("0")+" 秒数据（不足 5 分钟时保留已有数据） · "+r.Error;
            _table.Columns.Add("time","本地采样时间");_table.Columns.Add("state","状态");
            var fields=new List<FieldInfo>();foreach(var field in typeof(Snapshot).GetFields())if(field.FieldType==typeof(double)){fields.Add(field);_table.Columns.Add(field.Name,Label(field.Name));}
            _table.Columns.Add("cores","各逻辑处理器（%）");_table.Columns.Add("engines","GPU 引擎（%）");_table.Columns.Add("volumes","磁盘分区（容量/可用 GB）");_table.Columns.Add("uptime","设备运行时长");
            foreach(var sample in r.Samples)
            {
                var values=new List<object>{sample.ReceivedUtc.ToLocalTime().ToString("HH:mm:ss.fff"),sample.Data.Ok?"有效":"连接异常"};var s=sample.Data;
                foreach(var field in fields){double value=(double)field.GetValue(s);values.Add(!s.Ok||Double.IsNaN(value)||Double.IsInfinity(value)||(!s.HasGpu&&field.Name.StartsWith("Gpu"))?"—":Scale(field.Name,value).ToString("0.00"));}
                values.Add(s.Ok?String.Join(" / ",s.Cores.ConvertAll(delegate(double v){return v.ToString("0");}).ToArray()):"—");
                var engines=new List<string>();foreach(var item in s.Engines)engines.Add(item.Key+" "+item.Value.ToString("0"));values.Add(s.Ok?String.Join(" / ",engines.ToArray()):"—");
                var volumes=new List<string>();foreach(var v in s.Volumes)volumes.Add(v.Drive+" "+(v.SizeBytes/1073741824.0).ToString("0.0")+" / "+(v.FreeBytes/1073741824.0).ToString("0.0"));values.Add(s.Ok?String.Join("; ",volumes.ToArray()):"—");values.Add(s.Ok?s.Uptime:"—");_table.Rows.Add(values.ToArray());
            }
            RenderChart();
        }
        static double Scale(string name,double value){return name.EndsWith("Bytes")||name.StartsWith("Vram")?value/1073741824.0:name.EndsWith("Bps")?value/1048576.0:value;}
        static string Label(string name)
        {
            var labels=new Dictionary<string,string>{{"CpuPct","CPU %"},{"CpuMhz","CPU MHz"},{"MemPct","内存 %"},{"MemUsedBytes","已用内存 GB"},{"MemTotalBytes","总内存 GB"},{"MemAvailBytes","可用内存 GB"},{"MemCommittedBytes","提交内存 GB"},{"RamTotalBytes","物理内存 GB"},{"DiskActive","磁盘活动 %"},{"DiskReadBps","磁盘读取 MB/s"},{"DiskWriteBps","磁盘写入 MB/s"},{"DiskQueue","磁盘队列"},{"NetRxBps","网络接收 MB/s"},{"NetTxBps","网络发送 MB/s"},{"GpuPct","GPU %"},{"GpuTempC","GPU 温度 °C"},{"SysTempC","系统温度 °C"},{"GpuFanRpm","GPU 风扇 RPM"},{"GpuPowerPct","GPU 功耗 % TDP"},{"VramUsed","显存 GB"},{"VramTotal","显存总量 GB"},{"VramShared","共享显存 GB"}};string label;return labels.TryGetValue(name,out label)?label:name;
        }
        void Add(string title,Color color,Func<Snapshot,double> value)
        {
            var series=new Series(title){ChartType=SeriesChartType.Line,BorderWidth=2,Color=color,XValueType=ChartValueType.DateTime};_chart.Series.Add(series);
            Series current=series;int segment=0;
            foreach(var sample in _selected.Samples)
            {
                double v=sample.Data.Ok?value(sample.Data):Double.NaN;
                if(Double.IsNaN(v)||Double.IsInfinity(v)){current=null;continue;}
                if(current==null)
                {
                    if(series.Points.Count==0)current=series;
                    else{current=new Series(title+" #"+(++segment)){ChartType=SeriesChartType.Line,BorderWidth=2,Color=color,XValueType=ChartValueType.DateTime,IsVisibleInLegend=false};_chart.Series.Add(current);}
                }
                current.Points.Add(new DataPoint(sample.ReceivedUtc.ToLocalTime().ToOADate(),v));
            }
        }
        void RenderChart()
        {
            _chart.Series.Clear();var area=_chart.ChartAreas[0];area.BackColor=Bg;area.AxisX.LabelStyle.ForeColor=Fg;area.AxisY.LabelStyle.ForeColor=Fg;area.AxisX.LabelStyle.Format="HH:mm:ss";area.AxisX.MajorGrid.LineColor=Color.FromArgb(43,57,74);area.AxisY.MajorGrid.LineColor=Color.FromArgb(43,57,74);area.AxisX.LineColor=Color.SlateGray;area.AxisY.LineColor=Color.SlateGray;area.AxisX.StripLines.Clear();area.AxisY.Minimum=0;area.AxisY.Maximum=Double.NaN;
            if(_selected==null){area.AxisX.Minimum=Double.NaN;area.AxisX.Maximum=Double.NaN;return;}
            switch(_metric.SelectedIndex)
            {
                case 0:Add("GPU °C",Color.Orange,delegate(Snapshot s){return s.GpuTempC;});Add("系统 °C",Color.LightSkyBlue,delegate(Snapshot s){return s.SysTempC;});break;
                case 1:area.AxisY.Maximum=100;Add("CPU %",Color.LightSkyBlue,delegate(Snapshot s){return s.CpuPct;});Add("内存 %",Color.MediumPurple,delegate(Snapshot s){return s.MemPct;});Add("GPU %",Color.Orange,delegate(Snapshot s){return s.HasGpu?s.GpuPct:Double.NaN;});Add("磁盘 %",Color.LightGreen,delegate(Snapshot s){return s.DiskActive;});break;
                case 2:Add("已用 GB",Color.MediumPurple,delegate(Snapshot s){return s.MemUsedBytes/1073741824.0;});Add("可用 GB",Color.LightGreen,delegate(Snapshot s){return s.MemAvailBytes/1073741824.0;});Add("提交 GB",Color.Orange,delegate(Snapshot s){return s.MemCommittedBytes/1073741824.0;});break;
                case 3:Add("显存 GB",Color.Orange,delegate(Snapshot s){return s.VramUsed/1073741824.0;});Add("共享 GB",Color.MediumPurple,delegate(Snapshot s){return s.VramShared/1073741824.0;});break;
                case 4:Add("接收 MB/s",Color.LightSkyBlue,delegate(Snapshot s){return s.NetRxBps/1048576.0;});Add("发送 MB/s",Color.MediumPurple,delegate(Snapshot s){return s.NetTxBps/1048576.0;});break;
                case 5:Add("读 MB/s",Color.LightGreen,delegate(Snapshot s){return s.DiskReadBps/1048576.0;});Add("写 MB/s",Color.Orange,delegate(Snapshot s){return s.DiskWriteBps/1048576.0;});break;
                case 6:Add("CPU MHz",Color.LightSkyBlue,delegate(Snapshot s){return s.CpuMhz;});break;
                case 7:Add("风扇 RPM",Color.LightGreen,delegate(Snapshot s){return s.GpuFanRpm;});break;
                case 8:Add("功耗 % TDP",Color.Orange,delegate(Snapshot s){return s.GpuPowerPct;});break;
                case 9:Add("磁盘队列",Color.LightGreen,delegate(Snapshot s){return s.DiskQueue;});break;
            }
            double end=_selected.FaultUtc.ToLocalTime().ToOADate();area.AxisX.Minimum=Math.Min(_selected.Samples[0].ReceivedUtc.ToLocalTime().ToOADate(),end-1.0/86400);area.AxisX.Maximum=end+1.0/86400;
            area.AxisX.StripLines.Add(new StripLine{IntervalOffset=end,BorderColor=Color.Salmon,BorderWidth=2,BorderDashStyle=ChartDashStyle.Dash,Text="连接异常",TextLineAlignment=StringAlignment.Near,ForeColor=Color.Salmon});
        }
        protected override void Dispose(bool disposing){if(disposing){_refresh.Dispose();_font.Dispose();if(Icon!=null)Icon.Dispose();}base.Dispose(disposing);}
    }
}
