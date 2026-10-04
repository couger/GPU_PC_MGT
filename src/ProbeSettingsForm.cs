using System;
using System.Drawing;
using System.IO;
using System.Net.NetworkInformation;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Gpm
{
    static class SshStatus
    {
        public static string Read()
        {
            try
            {
                using(var service=new ServiceController("sshd"))
                {
                    bool running=service.Status==ServiceControllerStatus.Running;
                    if(!running)return "服务未运行";
                    int port=22;string path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"ssh","sshd_config");
                    try{var match=Regex.Match(File.ReadAllText(path),@"(?im)^\s*Port\s+(\d+)\s*(?:#.*)?$");if(match.Success)port=Int32.Parse(match.Groups[1].Value);}catch{}
                    bool listening=false;foreach(var endpoint in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())if(endpoint.Port==port)listening=true;
                    return "服务运行 · :"+port+(listening?" 有 TCP 监听":" 未发现 TCP 监听");
                }
            }
            catch(InvalidOperationException){return "未安装 OpenSSH 服务";}
            catch{return "状态无法读取";}
        }
    }
    class ProbeSettingsForm:Form
    {
        readonly string _path;ProbeConfig _config;
        TextBox host,bind,port,allowed,interval,idle;Label status;
        public ProbeSettingsForm(string path)
        {
            _path=path;_config=ProbeConfig.Load(path);
            Text="探针设置 · 连接与访问";ClientSize=new Size(580,510);StartPosition=FormStartPosition.CenterScreen;
            BackColor=Color.FromArgb(22,31,46);ForeColor=Color.White;Font=new Font("Microsoft YaHei UI",10);Icon=AppIcons.Load("probe",new Size(32,32));
            FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;
            Label intro=new Label{Text="监控端主动读取数据。探针只响应允许的地址及有效凭据。",Location=new Point(20,15),Size=new Size(540,45)};Controls.Add(intro);
            host=Field("连接地址",_config.Host,65);bind=Field("监听地址",_config.Bind,105);port=Field("监听端口",_config.Port.ToString(),145);
            allowed=Field("允许的监控端",_config.Allowed,185);allowed.Multiline=true;allowed.Height=60;
            Controls.Add(new Label{Text="多个 IP / CIDR 网段用逗号分隔；留空仅本机，* 允许所有地址。",Location=new Point(20,250),Size=new Size(540,25)});
            interval=Field("忙时采样（秒）",_config.Interval.ToString(),285);idle=Field("闲时采样（秒）",_config.Idle.ToString(),325);
            Controls.Add(new Label{Text="SSH："+SshStatus.Read()+"\n监听不代表防火墙已放行；本页不会开启 SSH。",Location=new Point(20,365),Size=new Size(540,45)});
            Button save=Button("保存配置",20,420,115);save.Click+=delegate{try{SaveFields();status.Text="已保存，探针会自动应用；修改端口请检查防火墙规则。";}catch(Exception ex){status.Text=ex.Message;}};
            Button copy=Button("复制连接凭据",145,420,145);copy.Click+=delegate{try{SaveFields();Clipboard.SetText(_config.Profile());status.Text="已复制连接信息，在监控端设置中点击“粘贴连接凭据”。";}catch(Exception ex){status.Text=ex.Message;}};
            Button rotate=Button("重新生成凭据",300,420,145);rotate.Click+=delegate
            {
                if(MessageBox.Show(this,"重新生成后旧凭据失效，所有监控端需要重新粘贴连接凭据。继续？","更新凭据",MessageBoxButtons.OKCancel,MessageBoxIcon.Warning)!=DialogResult.OK)return;
                try{var next=ReadFields();next.SetToken(ProbeConfig.NewToken());next.Save(_path);_config=next;status.Text="新凭据已保存，请复制给监控端。";}catch(Exception ex){status.Text=ex.Message;}
            };
            status=new Label{Location=new Point(20,462),Size=new Size(540,42),ForeColor=Color.LightSkyBlue};Controls.Add(status);
        }
        TextBox Field(string label,string value,int y)
        {
            Controls.Add(new Label{Text=label,Location=new Point(20,y+4),Size=new Size(150,25)});
            var t=new TextBox{Text=value,Location=new Point(180,y),Size=new Size(375,29)};Controls.Add(t);return t;
        }
        Button Button(string label,int x,int y,int width){var b=new Button{Text=label,Location=new Point(x,y),Size=new Size(width,32),ForeColor=Color.FromArgb(18,30,46),BackColor=Color.LightSkyBlue,UseVisualStyleBackColor=false,FlatStyle=FlatStyle.Flat};Controls.Add(b);return b;}
        ProbeConfig ReadFields(){var c=new ProbeConfig{Host=host.Text.Trim(),Bind=bind.Text.Trim(),Port=Int32.Parse(port.Text),Allowed=allowed.Text.Trim(),Interval=Int32.Parse(interval.Text),Idle=Int32.Parse(idle.Text),TokenProtected=_config.TokenProtected};c.Validate();return c;}
        void SaveFields(){var next=ReadFields();next.Save(_path);_config=next;}
    }
}
