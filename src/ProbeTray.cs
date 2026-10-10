using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Gpm
{
    // Runs in the signed-in user's desktop. The SYSTEM probe keeps collecting in session 0.
    static class ProbeTray
    {
        sealed class Status { public string Text="探针状态\n  正在检查…";public bool Warning; public bool Ready; }
        static volatile Status _status=new Status();
        static volatile bool _stopping;
        static int _port=9187;
        static string _token="";
        static string _dir;
        static string _configPath=ProbeConfig.DefaultPath;
        static string _summary="";
        static string _pollHost="127.0.0.1";
        static ProbePowerReminder _powerReminder;
        static readonly IProbePowerModes _powerModes=new WindowsProbePowerModes();
        static ProbePowerReader _powerReader;
        [STAThread]
        static int Main(string[] args)
        {
            bool first;
            using(Mutex mutex=new Mutex(true,"Local\\GPUPCMGT.ProbeTray",out first))
            {
                if(!first)return 0;
                _dir=AppDomain.CurrentDomain.BaseDirectory;Config.Dir=_dir;Config.DataDir=_dir;
                try
                {
                    if(File.Exists(_configPath)){var settings=ProbeConfig.Load(_configPath);_port=settings.Port;_token=settings.Token;}
                    else {
                    string path=args.Length>0?args[0]:Path.Combine(_dir,"probe-tray.json");
                    Dictionary<string,object> cfg=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(path));
                    _port=Convert.ToInt32(cfg["port"]);
                    _token=Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(Convert.ToString(cfg["tokenProtected"])),null,DataProtectionScope.CurrentUser));
                    }
                }
                catch(Exception ex) { UiLog.W("probe tray config: "+ex.Message);_status=new Status{Text="探针状态 · 配置异常\n  请重新安装状态图标",Warning=true}; }
                Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
                using(Form owner=new Form { Text="GPU PC 探针状态", ShowInTaskbar=false })
                using(TrayIcon tray=new TrayIcon(owner,"探针状态\n  正在检查…","probe"))
                using(System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer())
                using(var powerReader=new ProbePowerReader())
                using(var powerReminder=new ProbePowerReminder(Path.Combine(_dir,"probe-ui.json"),powerReader.Read))
                {
                    _powerReminder=powerReminder;
                    _powerReader=powerReader;
                    tray.Clicked+=delegate { ShowMenu(owner,tray); };
                    tray.MenuRequested+=delegate { ShowMenu(owner,tray); };
                    Thread worker=new Thread(Poll);worker.IsBackground=true;worker.Start();
                    bool? previousWarning=null;
                    timer.Interval=1000;timer.Tick+=delegate
                    {
                        Status s=_status;tray.Update(s.Text);tray.SetWarning(s.Warning);
                        try{powerReminder.Tick(DateTime.UtcNow);}catch(Exception ex){UiLog.W("power reminder tick: "+ex.GetType().Name);}
                        if(s.Ready && previousWarning!=s.Warning)
                        {
                            UiLog.W("probe endpoint healthy="+!s.Warning+" tray registered="+tray.Added+" desktop session="+Process.GetCurrentProcess().SessionId);
                            previousWarning=s.Warning;
                        }
                    };timer.Start();
                    Application.Run(new ApplicationContext());
                    _stopping=true;timer.Stop();worker.Join(2500);
                }
                mutex.ReleaseMutex();return 0;
            }
        }
        static void ShowMenu(Form owner,TrayIcon tray)
        {
            tray.HideFlyout();
            bool plugged=true;ProbePowerMode mode=ProbePowerMode.Unknown;string scope="状态未知";
            try{plugged=WindowsProbePowerModes.PluggedIn;mode=_powerModes.Get(plugged);scope=plugged?"接通电源":"使用电池";}catch(Exception ex){UiLog.W("power menu read: "+ex.GetType().Name);}
            int command=TrayMenu.ShowChecked(owner,mode==ProbePowerMode.Unknown?0:7+(int)mode,"探针状态："+(_status.Warning?"异常":"运行中"),"探针设置…","复制连接凭据","打开探针目录","-","电源模式（"+scope+"）","最佳能效","平衡","最佳性能",(_powerReminder.Enabled?"关闭":"开启")+"性能模式提醒","-","退出状态图标（保留采集）");
            try{
            if(command==2)using(var form=new ProbeSettingsForm(_configPath,_powerModes,_powerReader.Read))form.ShowDialog();
            else if(command==3)Clipboard.SetText(ProbeConfig.Load(_configPath).Profile());
            else if(command==4)Process.Start("explorer.exe",_dir);
            else if(command>=7&&command<=9){bool currentSource=WindowsProbePowerModes.PluggedIn;if(currentSource!=plugged)throw new InvalidOperationException("供电方式刚刚变化，请重新打开菜单选择电源模式。");_powerModes.Set(plugged,(ProbePowerMode)(command-7));UiLog.W("user power mode set: source="+(plugged?"AC":"DC")+" mode="+(command-7));if(_powerReader.Read().EnergySaver>0)tray.Balloon("电源模式已保存","节能模式仍开启，可能继续限制性能。可按 Win + A 关闭节能模式。");}
            else if(command==10)_powerReminder.SetEnabled(!_powerReminder.Enabled);
            else if(command==12)Application.ExitThread();
            }catch(Exception ex){MessageBox.Show(ex.Message,"探针配置");}
        }
        static string Number(Dictionary<string,object> json,string key,string suffix)
        { object value;return json.TryGetValue(key,out value)&&value!=null?Convert.ToDouble(value).ToString("0.0")+suffix:"—"; }
        static void Poll()
        {
            while(!_stopping)
            {
                try
                {
                    if(File.Exists(_configPath))
                    {
                        var settings=ProbeConfig.Load(_configPath);_port=settings.Port;_token=settings.Token;
                        _pollHost=settings.Bind=="0.0.0.0"?"127.0.0.1":settings.Bind=="::"?"::1":settings.Bind;
                        _summary="\n  监听    "+settings.Bind+":"+settings.Port+"\n  允许    "+(String.IsNullOrWhiteSpace(settings.Allowed)?"仅本机":settings.Allowed)+"\n  采样    忙 "+settings.Interval+"秒 / 闲 "+settings.Idle+"秒";
                    }
                    using(TcpClient client=new TcpClient())
                    {
                        IAsyncResult connect=client.BeginConnect(_pollHost,_port,null,null);
                        using(connect.AsyncWaitHandle)if(!connect.AsyncWaitHandle.WaitOne(1500))throw new IOException("端点未响应");
                        client.EndConnect(connect);client.ReceiveTimeout=1500;client.SendTimeout=1500;
                        using(NetworkStream stream=client.GetStream())
                        using(StreamReader reader=new StreamReader(stream,Encoding.UTF8))
                        {
                            byte[] auth=Encoding.ASCII.GetBytes(_token+"\n");stream.Write(auth,0,auth.Length);
                            string line=reader.ReadLine();if(line==null||line.Length>65536)throw new IOException("无有效采样");
                            Dictionary<string,object> j=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(line);
                            object error;if(j.TryGetValue("error",out error))throw new IOException("探针暂不可用");
                            double total=Convert.ToDouble(j["memTotal"]),avail=Convert.ToDouble(j["memAvail"]);
                            string memory=total>0?((total-avail)/total*100).ToString("0.0")+"%  ·  "+((total-avail)/1073741824.0).ToString("0.0")+" / "+(total/1073741824.0).ToString("0.0")+" GB":"—";
                            _status=new Status{Ready=true,Text=Environment.MachineName+" · 探针运行中\n  CPU    "+Number(j,"cpuPct","%")+"\n  内存    "+memory+"\n  GPU    "+Number(j,"gpuPct","%")+"  ·  "+Number(j,"gpuTemp"," °C")+_summary+"\n  SSH    "+SshStatus.Read()+"\n  检查    "+DateTime.Now.ToString("HH:mm:ss")};
                        }
                    }
                }
                catch { _status=new Status{Ready=true,Text=Environment.MachineName+" · 探针连接异常\n  采集进程停止或端点暂不可用"+_summary+"\n  SSH    "+SshStatus.Read()+"\n  检查    "+DateTime.Now.ToString("HH:mm:ss"),Warning=true}; }
                for(int i=0;i<30&&!_stopping;i++)Thread.Sleep(100);
            }
        }
    }
    static class UiLog
    {
        public static void W(string text) { LocalFiles.Append(Path.Combine(Config.DataDir,"probe-tray.log"),DateTime.Now.ToString("s")+" "+text+Environment.NewLine,65536); }
    }
}
