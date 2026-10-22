using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Gpm
{
    public sealed class ProbePowerPreferences
    {
        public bool Enabled=true;
        public static ProbePowerPreferences Load(string path)
        {
            try { if(File.Exists(path))return new JavaScriptSerializer().Deserialize<ProbePowerPreferences>(File.ReadAllText(path))??new ProbePowerPreferences(); }
            catch(Exception ex){UiLog.W("power reminder preference read failed: "+ex.GetType().Name);}
            return new ProbePowerPreferences();
        }
        public void Save(string path)
        {
            File.WriteAllText(path,new JavaScriptSerializer().Serialize(this));
        }
    }
    public sealed class ProbePowerState
    {
        public int Effective=-1,EnergySaver=-1;
        public bool Known { get { return EnergySaver>0 || Effective>=0; } }
        public bool Optimal { get { return EnergySaver<=0 && (Effective==3 || Effective==4); } }
        public string Description { get {
            if(EnergySaver==2)return "节能模式（高节能）";
            if(EnergySaver==1)return "节能模式";
            switch(Effective){case 0:return "电池节电模式";case 1:return "最佳能效";case 2:return "平衡";case 3:return "高性能";case 4:return "最佳性能";case 5:return "游戏性能模式";case 6:return "混合现实性能模式";default:return "正在读取电源状态";}
        } }
    }
    // Callback registration immediately returns the current state, then follows changes.
    // Keep delegates alive until native registrations are disposed; never touch UI on callbacks.
    public sealed class ProbePowerReader : IDisposable
    {
        delegate void ModeCallback(int mode,IntPtr context);
        delegate uint SettingCallback(IntPtr context,uint type,IntPtr data);
        [StructLayout(LayoutKind.Sequential)]struct Subscription {public SettingCallback Callback;public IntPtr Context;}
        [DllImport("powrprof.dll")]static extern uint PowerRegisterForEffectivePowerModeNotifications(uint version,ModeCallback callback,IntPtr context,out IntPtr handle);
        [DllImport("powrprof.dll")]static extern uint PowerUnregisterFromEffectivePowerModeNotifications(IntPtr handle);
        [DllImport("powrprof.dll")]static extern uint PowerSettingRegisterNotification(ref Guid guid,uint flags,ref Subscription subscription,out IntPtr handle);
        [DllImport("powrprof.dll")]static extern uint PowerSettingUnregisterNotification(IntPtr handle);
        readonly ModeCallback _modeCallback;
        readonly SettingCallback _settingCallback;
        IntPtr _modeHandle,_settingHandle;
        int _mode=-1,_energy=-1;
        bool _disposed;
        public ProbePowerReader()
        {
            _modeCallback=delegate(int mode,IntPtr context){Interlocked.Exchange(ref _mode,mode);};
            _settingCallback=delegate(IntPtr context,uint type,IntPtr data){
                if(type==0x8013 && data!=IntPtr.Zero && Marshal.ReadInt32(data,16)>=4)Interlocked.Exchange(ref _energy,Marshal.ReadInt32(data,20));return 0;
            };
            try {
                // V1 follows the power slider without temporary game/MR overrides.
                uint result=PowerRegisterForEffectivePowerModeNotifications(1,_modeCallback,IntPtr.Zero,out _modeHandle);
                if(result!=0){_modeHandle=IntPtr.Zero;UiLog.W("power mode registration failed: "+result);}
                Guid guid=new Guid("550e8400-e29b-41d4-a716-446655440000");
                Subscription subscription=new Subscription{Callback=_settingCallback};
                result=PowerSettingRegisterNotification(ref guid,2,ref subscription,out _settingHandle);
                if(result!=0){_settingHandle=IntPtr.Zero;UiLog.W("energy saver registration unavailable: "+result);}
            }catch(Exception ex){UiLog.W("power reminder detection unavailable: "+ex.GetType().Name);}
        }
        public ProbePowerState Read() {return new ProbePowerState{Effective=Volatile.Read(ref _mode),EnergySaver=Volatile.Read(ref _energy)};}
        public void Dispose()
        {
            if(_disposed)return;_disposed=true;
            if(_modeHandle!=IntPtr.Zero)PowerUnregisterFromEffectivePowerModeNotifications(_modeHandle);
            if(_settingHandle!=IntPtr.Zero)PowerSettingUnregisterNotification(_settingHandle);
            GC.KeepAlive(_modeCallback);GC.KeepAlive(_settingCallback);
        }
    }
    public sealed class ProbePowerNotice : Form
    {
        readonly Label _mode;
        readonly Font _headingFont;
        bool _resourcesDisposed;
        protected override bool ShowWithoutActivation {get{return true;}}
        public ProbePowerNotice()
        {
            Text="GPU 探针 · 性能模式提醒";Icon=AppIcons.Load("probe-warning",new Size(32,32));
            AutoScaleMode=AutoScaleMode.Dpi;ClientSize=new Size(500,256);
            FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
            ShowInTaskbar=false;TopMost=true;StartPosition=FormStartPosition.CenterScreen;
            BackColor=SystemColors.Window;Font=new Font("Microsoft YaHei UI",10);
            _headingFont=new Font(Font.FontFamily,16,FontStyle.Bold);
            var title=new Label{Text="当前未处于最佳性能状态",ForeColor=Color.FromArgb(160,65,0),Font=_headingFont,Location=new Point(24,22),Size=new Size(452,37)};
            _mode=new Label{Location=new Point(24,71),Size=new Size(452,27)};
            var body=new Label{Text="GPU 计算性能可能受到限制。\r\n请关闭节能模式，并在 Windows 电源设置中选择最佳性能。",Location=new Point(24,105),Size=new Size(452,52)};
            var hint=new Label{Text="关闭后 60 秒再次提醒；可在探针托盘菜单中关闭此功能。",ForeColor=SystemColors.GrayText,Location=new Point(24,168),Size=new Size(452,25)};
            var settings=new Button{Text="打开电源设置",Location=new Point(24,205),Size=new Size(150,32)};
            settings.Click+=delegate{try{Process.Start(new ProcessStartInfo("ms-settings:powersleep"){UseShellExecute=true});}catch(Exception ex){MessageBox.Show(this,ex.Message,"无法打开设置");}};
            var later=new Button{Text="稍后提醒（60 秒）",Location=new Point(304,205),Size=new Size(172,32)};later.Click+=delegate{Close();};
            Controls.AddRange(new Control[]{title,_mode,body,hint,settings,later});
        }
        public void UpdateState(ProbePowerState state){_mode.Text="当前电源状态："+state.Description;}
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if(disposing&&!_resourcesDisposed){_resourcesDisposed=true;_headingFont.Dispose();Font.Dispose();if(Icon!=null)Icon.Dispose();}
        }
    }
    public sealed class ProbePowerReminder : IDisposable
    {
        readonly string _path;
        readonly Func<ProbePowerState> _read;
        ProbePowerPreferences _prefs;
        ProbePowerNotice _notice;
        DateTime _due=DateTime.MinValue;
        bool _disposed;
        string _lastState="";
        public ProbePowerReminder(string path,Func<ProbePowerState> read){_path=path;_read=read;_prefs=ProbePowerPreferences.Load(path);}
        public bool Enabled {get{return _prefs.Enabled;}}
        public ProbePowerNotice Notice {get{return _notice;}}
        public void SetEnabled(bool enabled)
        {
            var next=new ProbePowerPreferences{Enabled=enabled};next.Save(_path);_prefs=next;_due=DateTime.MinValue;
            if(!enabled)CloseNotice();UiLog.W("power reminder enabled="+enabled);
        }
        public void Tick(DateTime utc)
        {
            if(_disposed)return;var state=_read();
            string status=state.Effective+"/"+state.EnergySaver;
            if(status!=_lastState){UiLog.W("power reminder state="+status+" optimal="+state.Optimal);_lastState=status;}
            if(!Enabled||!state.Known||state.Optimal){CloseNotice();_due=DateTime.MinValue;return;}
            if(_notice!=null){_notice.UpdateState(state);return;}
            if(utc<_due)return;
            var notice=new ProbePowerNotice();notice.UpdateState(state);_notice=notice;
            notice.FormClosed+=delegate{if(_notice==notice)_notice=null;_due=DateTime.UtcNow.AddSeconds(60);notice.Dispose();};
            notice.Show();UiLog.W("power reminder shown");
        }
        void CloseNotice(){if(_notice!=null)_notice.Close();}
        public void Dispose(){if(_disposed)return;_disposed=true;CloseNotice();}
    }
}
