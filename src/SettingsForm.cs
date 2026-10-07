using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Windows.Forms;

namespace Gpm
{
    /// <summary>界面侧的小日志（托盘是否建成、设置保存等），写到 dataDir\ui.log，便于排错</summary>
    static class UiLog
    {
        public static void W(string msg)
        {
            try
            {
                string dir = Config.DataDir.Length > 0 ? Config.DataDir : Config.Dir;
                string p = Path.Combine(dir, "ui.log");
                LocalFiles.Append(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + "\r\n",65536);
            }
            catch { }
        }
    }

    /// <summary>设置窗口：在界面里增删改目标与通道（probe / ssh / local），保存后写回 config.json</summary>
    class SettingsForm : Form
    {
        readonly List<Target> _targets;
        readonly string _cfgPath;
        ListBox _list;
        TextBox _name, _host, _port, _token, _user, _pass, _key, _interval, _idle;
        ComboBox _kind;
        CheckBox _enabled, _closeToTray;
        Label _status;
        bool _loading;

        public SettingsForm(List<Target> targets, string cfgPath)
        {
            _targets = targets;
            _cfgPath = cfgPath;
            Text = "设置 · 目标与通道";
            ClientSize = new Size(660, 520);
            BackColor = Theme.Bg;
            ForeColor = Theme.Fg;
            Font = Theme.FBody;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            Controls.Add(Lbl("目标列表", 12, 10));
            _list = new ListBox();
            _list.Location = new Point(12, 32);
            _list.Size = new Size(210, 400);
            _list.BackColor = Color.FromArgb(45, 45, 45);
            _list.ForeColor = Theme.Fg;
            _list.BorderStyle = BorderStyle.FixedSingle;
            _list.SelectedIndexChanged += delegate { LoadSelected(); };
            Controls.Add(_list);

            int x = 216, y = 32, dy = 29;
            Controls.Add(Lbl("名称", x, y + 3)); _name = Tb(x + 82, y, 290); y += dy;
            Controls.Add(Lbl("通道", x, y + 3)); _kind = Cmb(x + 82, y, 290); y += dy;
            Controls.Add(Lbl("主机/IP", x, y + 3)); _host = Tb(x + 82, y, 290); y += dy;
            Controls.Add(Lbl("端口", x, y + 3)); _port = Tb(x + 82, y, 290); y += dy;
            Controls.Add(Lbl("令牌", x, y + 3)); _token = Tb(x + 82, y, 290); _token.UseSystemPasswordChar=true; y += dy;
            Controls.Add(Lbl("用户名", x, y + 3)); _user = Tb(x + 82, y, 290); y += dy;
            Controls.Add(Lbl("密码", x, y + 3)); _pass = Tb(x + 82, y, 290); _pass.UseSystemPasswordChar = true; y += dy;
            Controls.Add(Lbl("密钥文件", x, y + 3)); _key = Tb(x + 82, y, 290); y += dy;
            Controls.Add(Lbl("忙间隔", x, y + 3)); _interval = Tb(x + 82, y, 50);
            Controls.Add(Lbl("秒", x + 136, y + 3));
            Controls.Add(Lbl("闲间隔", x + 166, y + 3)); _idle = Tb(x + 226, y, 50);
            Controls.Add(Lbl("秒", x + 280, y + 3)); y += dy;

            _enabled = new CheckBox();
            _enabled.Text = "启用该目标";
            _enabled.ForeColor = Theme.Fg;
            _enabled.AutoSize = true;
            _enabled.Location = new Point(x + 82, y + 3);
            Controls.Add(_enabled);
            _closeToTray = new CheckBox();
            _closeToTray.Text = "点 ✕ / 最小化时收进托盘";
            _closeToTray.ForeColor = Theme.Fg;
            _closeToTray.AutoSize = true;
            _closeToTray.Location = new Point(x + 82, y + 26);
            _closeToTray.Checked = MainForm.CloseToTray;
            Controls.Add(_closeToTray);
            y += 54;

            Button add = Btn("新增", x + 82, y, 62); add.Click += delegate { AddTarget(); };
            Button del = Btn("删除", x + 150, y, 62); del.Click += delegate { DelTarget(); };
            Button test = Btn("测试", x + 218, y, 62); test.Click += delegate { DoTest(); };
            Button save = Btn("保存并重启", x + 286, y, 86); save.Click += delegate { Save(); };
            Controls.Add(add); Controls.Add(del); Controls.Add(test); Controls.Add(save);
            y += 38;

            _status = new Label();
            _status.Location = new Point(x + 82, y);
            _status.Size = new Size(300, 90);
            _status.ForeColor = Theme.Dim;
            _status.Text = "probe：填 主机/IP + 端口(9187) + 令牌\nssh：填 主机/IP + 用户名 + 密码（或密钥文件）\nlocal：本机，无需填写";
            Controls.Add(_status);
            Button paste=Btn("粘贴连接凭据",12,445,210);paste.Click+=delegate
            {
                try{var profile=ConnectionProfile.Parse(Clipboard.GetText());_kind.SelectedItem="probe";_name.Text=profile.Name;_host.Text=profile.Host;_port.Text=profile.Port.ToString();_token.Text=profile.Token;_status.Text="已填入探针连接信息，请测试并保存。";}
                catch(Exception ex){_status.Text="无法粘贴："+ex.Message;}
            };Controls.Add(paste);

            RefreshList();
        }

        // ---------------------------------------------------------------- helpers
        static Label Lbl(string t, int x, int y)
        {
            Label l = new Label();
            l.Text = t; l.ForeColor = Theme.Dim; l.AutoSize = true; l.Location = new Point(x, y);
            return l;
        }

        TextBox Tb(int x, int y, int w)
        {
            TextBox t = new TextBox();
            t.Location = new Point(x, y); t.Width = w;
            t.BackColor = Color.FromArgb(45, 45, 45); t.ForeColor = Theme.Fg;
            t.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(t);
            return t;
        }

        ComboBox Cmb(int x, int y, int w)
        {
            ComboBox c = new ComboBox();
            c.Location = new Point(x, y); c.Width = w;
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.FlatStyle = FlatStyle.Flat;
            c.BackColor = Color.FromArgb(45, 45, 45); c.ForeColor = Theme.Fg;
            c.Items.AddRange(new object[] { "probe", "ssh", "local" });
            c.SelectedIndex = 0;
            c.SelectedIndexChanged += delegate { SyncFields(); };
            Controls.Add(c);
            return c;
        }

        Button Btn(string t, int x, int y, int w)
        {
            Button b = new Button();
            b.Text = t; b.Location = new Point(x, y); b.Width = w; b.Height = 26;
            b.FlatStyle = FlatStyle.Flat; b.BackColor = Color.FromArgb(51, 51, 51); b.ForeColor = Theme.Fg;
            return b;
        }

        void SyncFields()
        {
            string k = _kind.SelectedItem as string;
            bool probe = k == "probe";
            bool ssh = k == "ssh";
            bool loc = k == "local";
            _host.Enabled = !loc;
            _port.Enabled = probe;
            _token.Enabled = probe;
            _user.Enabled = ssh;
            _pass.Enabled = ssh;
            _key.Enabled = ssh;
            if (probe && _port.Text.Trim().Length == 0) _port.Text = "9187";
            if (ssh && _port.Text.Trim() == "9187") _port.Text = "22";
            _status.Text = probe
                ? "probe：在探针托盘中复制连接凭据，再点击“粘贴连接凭据”。\n凭据由探针生成；允许的监控端地址也在探针端配置。"
                : (ssh ? "ssh：每轮登录执行一次原生探针；链路加密，适合跨不可信网络。"
                       : "local：只监视本机（任务管理器已足够时通常不需要）。");
        }

        void RefreshList()
        {
            _loading = true;
            _list.Items.Clear();
            foreach (Target t in _targets) _list.Items.Add((t.Enabled ? "[启用] " : "[关闭] ") + t.Name + "  ·  " + ChannelShort(t));
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            _loading = false;
            LoadSelected();          // 初始选中也要把字段填上
        }

        static string ChannelShort(Target t)
        {
            if (t.Kind == "probe") return "probe " + t.Host + ":" + t.Port;
            if (t.Kind == "ssh") return "ssh " + t.Host;
            if (t.Kind == "local") return "本机";
            return t.Kind;
        }

        Target Current()
        {
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _targets.Count) return null;
            return _targets[i];
        }

        void LoadSelected()
        {
            if (_loading) return;
            Target t = Current();
            if (t == null) return;
            _name.Text = t.Name;
            _kind.SelectedItem = t.Kind == "probe" ? "probe" : (t.Kind == "ssh" ? "ssh" : "local");
            _host.Text = t.Host;
            _port.Text = t.Port.ToString();
            _token.Text = t.Token;
            _user.Text = t.User;
            _pass.Text = t.Password;
            _key.Text = t.KeyFile;
            _interval.Text = t.IntervalSec.ToString();
            _idle.Text = t.IdleIntervalSec.ToString();
            _enabled.Checked = t.Enabled;
            SyncFields();
        }

        void Apply()
        {
            Target t = Current();
            if (t == null) return;
            t.Name = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : "目标";
            t.Kind = (_kind.SelectedItem as string) ?? "probe";
            t.Host = _host.Text.Trim();
            int p = 22;
            if (t.Kind == "probe") p = 9187;
            int.TryParse(_port.Text.Trim(), out p);
            t.Port = p;
            t.Token = _token.Text.Trim();
            t.User = _user.Text.Trim();
            t.Password = _pass.Text;
            t.KeyFile = _key.Text.Trim();
            int iv = 2;
            int.TryParse(_interval.Text.Trim(), out iv);
            if (iv < 1) iv = 1;
            t.IntervalSec = iv;
            int idl = 0;
            int.TryParse(_idle.Text.Trim(), out idl);
            t.IdleIntervalSec = idl < 0 ? 0 : idl;
            t.Enabled = _enabled.Checked;
        }

        void AddTarget()
        {
            Apply();
            Target t = new Target();
            t.Name = "新目标";
            t.Kind = "probe";
            t.Port = 9187;
            t.Token = "";
            t.IntervalSec = 2;
            t.IdleIntervalSec = 10;
            t.Enabled = true;
            _targets.Add(t);
            RefreshList();
            _list.SelectedIndex = _targets.Count - 1;
            LoadSelected();
        }

        void DelTarget()
        {
            Target t = Current();
            if (t == null) return;
            if (_targets.Count <= 1) { _status.Text = "至少保留一个目标。"; return; }
            _targets.RemoveAt(_list.SelectedIndex);
            RefreshList();
        }

        void DoTest()
        {
            Apply();
            Target t = Current();
            if (t == null) return;
            _status.Text = "测试中…";
            Application.DoEvents();
            string result;
            if (t.Kind == "local") result = "local 无需测试（本机直采）";
            else if (t.Kind == "probe")
            {
                try
                {
                    using (TcpClient c = new TcpClient())
                    {
                        c.ReceiveTimeout = 5000; c.SendTimeout = 5000;
                        c.Connect(t.Host, t.Port);
                        using (NetworkStream ns = c.GetStream())
                        {
                            ns.ReadTimeout = 5000; ns.WriteTimeout = 5000;
                            byte[] b = Encoding.ASCII.GetBytes(t.Token + "\n");
                            ns.Write(b, 0, b.Length); ns.Flush();
                            byte[] buf = new byte[16384];
                            int n = ns.Read(buf, 0, buf.Length);
                            string s = Encoding.UTF8.GetString(buf, 0, n);
                            result = s.IndexOf("\"error\"") >= 0 ? "端点有响应但拒绝：" + s.Substring(0, Math.Min(80, s.Length))
                                                                 : "端点正常，返回 " + n + " 字节";
                        }
                    }
                }
                catch (Exception ex) { result = "连接失败：" + ex.Message; }
            }
            else
            {
                try
                {
                    RemoteSampler rs = new RemoteSampler(t);
                    Snapshot s = rs.Fetch(true);
                    result = s.Ok ? ("SSH 正常，" + s.CollectMs + " ms，主机 " + s.Host) : ("SSH 失败：" + s.Error);
                }
                catch (Exception ex) { result = "SSH 失败：" + ex.Message; }
            }
            _status.Text = result;
            UiLog.W("test " + t.Kind + " " + t.Host + ": " + result);
        }

        void Save()
        {
            Apply();
            foreach(Target target in _targets)if(target.Enabled&&target.Kind=="probe"&&(target.Token.Length<16||target.Token.Length>128)){_status.Text="请先从探针端复制连接凭据，再粘贴到监控端。";return;}
            MainForm.CloseToTray = _closeToTray.Checked;
            try
            {
                Config.Save(_cfgPath, _targets);
                UiLog.W("settings saved: " + _targets.Count + " targets, closeToTray=" + MainForm.CloseToTray);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                _status.Text = "保存失败：" + ex.Message;
                UiLog.W("settings save failed: " + ex.Message);
            }
        }
    }
}
