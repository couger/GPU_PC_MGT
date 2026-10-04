using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Gpm
{
    /// <summary>config.json 读写；口令用 DPAPI(当前用户) 加密，不明文落盘</summary>
    public static class Config
    {
        /// <summary>exe 所在目录（config.json、ssh-askpass.exe 在这里）</summary>
        public static string Dir = ".";
        /// <summary>运行期产物目录（状态、日志、报告、截图）。默认与 exe 同目录，可用 config.json 的 dataDir 或 --data 指到别的盘。</summary>
        public static string DataDir = ".";
        public static bool CloseToTray = true;

        public static string PathOf(string name)
        {
            return Path.Combine(DataDir, name);
        }

        public static List<Target> Load(string path)
        {
            CloseToTray = true;
            List<Target> list = new List<Target>();
            if (!File.Exists(path))
            {
                Target local = new Target();
                local.Name = "本机";
                local.Kind = "local";
                local.IntervalSec = 1;
                local.Enabled = false;      // 本机默认不启用（任务管理器就够了）
                list.Add(local);
                Save(path, list);
                return list;
            }

            string text = File.ReadAllText(path, Encoding.UTF8);
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = 8 * 1024 * 1024;
            IDictionary<string, object> root = js.DeserializeObject(text) as IDictionary<string, object>;
            if (root != null) CloseToTray = Num(root, "closeToTray", 1) != 0;
            object arrObj;
            bool needSave = false;
            // 运行期产物目录（可指到空间大的盘，例如 D:）
            object dd;
            if (root != null && root.TryGetValue("dataDir", out dd) && dd != null)
            {
                string d = Convert.ToString(dd).Trim();
                if (d.Length > 0)
                {
                    DataDir = Path.IsPathRooted(d) ? d : Path.GetFullPath(Path.Combine(Dir, d));
                    try { if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir); } catch { }
                }
            }
            System.Collections.IEnumerable items = null;
            if (root != null && root.TryGetValue("targets", out arrObj) && arrObj != null)
                items = arrObj as System.Collections.IEnumerable;
            if (items != null)
            {
                foreach (object item in items)
                {
                    IDictionary<string, object> d = item as IDictionary<string, object>;
                    if (d == null) continue;
                    Target t = new Target();
                    t.Name = Str(d, "name");
                    t.Kind = Str(d, "kind");
                    if (t.Kind.Length == 0) t.Kind = Str(d, "channel");
                    if (t.Kind.Length == 0) t.Kind = (Str(d, "host").Length > 0) ? "ssh" : "local";
                    t.Host = Str(d, "host");
                    t.User = Str(d, "user");
                    t.KeyFile = Str(d, "keyFile");
                    t.KnownHosts = Str(d, "knownHosts");
                    t.Port = (int)Num(d, "port", 22);
                    t.Token = Str(d, "token");
                    t.IntervalSec = (int)Num(d, "intervalSec", 2);
                    if (t.IntervalSec < 1) t.IntervalSec = 1;
                    t.Enabled = (Num(d, "enabled", 1) != 0);
                    t.IdleIntervalSec = (int)Num(d, "idleIntervalSec", 0);
                    if (t.IdleIntervalSec < 0) t.IdleIntervalSec = 0;
                    t.RemoteDir = Str(d, "remoteDir");
                    if (t.Name.Length == 0) t.Name = (t.Host.Length > 0) ? t.Host : "本机";
                    // 相对路径按 exe 所在目录解析
                    if (t.KnownHosts.Length > 0) t.KnownHosts = Path.GetFullPath(Path.Combine(Dir, t.KnownHosts));
                    if (t.KeyFile.Length > 0) t.KeyFile = Path.GetFullPath(Path.Combine(Dir, t.KeyFile));

                    string plain = Str(d, "password");
                    string prot = Str(d, "passwordProtected");
                    if (plain.Length > 0)
                    {
                        t.Password = plain;
                        d["passwordProtected"] = Protect(plain);
                        d.Remove("password");
                        needSave = true;
                    }
                    else if (prot.Length > 0)
                    {
                        t.Password = Unprotect(prot);
                    }
                    list.Add(t);
                }
            }
            // 保证总有一个"本机"目标存在（界面上那个勾选框要用它），默认不启用
            bool hasLocal = false;
            foreach (Target t in list) if (t.Kind == "local") hasLocal = true;
            if (!hasLocal)
            {
                Target lt = new Target();
                lt.Name = "本机";
                lt.Kind = "local";
                lt.IntervalSec = 1;
                lt.Enabled = false;
                list.Insert(0, lt);
            }
            if (needSave) Save(path, list);
            return list;
        }

        public static void Save(string path, List<Target> targets)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n  \"closeToTray\": ").Append(CloseToTray ? "true" : "false").Append(",\n");
            sb.Append("  \"_comment\": \"口令字段由程序自动用 DPAPI 加密为 passwordProtected（只有当前 Windows 用户能解密）；dataDir 可指向空间大的盘（留空=与 exe 同目录）\",\n");
            if (DataDir.Length > 0 && !string.Equals(Path.GetFullPath(DataDir), Path.GetFullPath(Dir), StringComparison.OrdinalIgnoreCase))
                sb.Append("  \"dataDir\": ").Append(Js(DataDir)).Append(",\n");
            sb.Append("  \"targets\": [\n");
            for (int i = 0; i < targets.Count; i++)
            {
                Target t = targets[i];
                sb.Append("    { \"name\": ").Append(Js(t.Name));
                sb.Append(", \"kind\": ").Append(Js(t.Kind));
                if (t.Host.Length > 0) sb.Append(", \"host\": ").Append(Js(t.Host));
                if (t.User.Length > 0) sb.Append(", \"user\": ").Append(Js(t.User));
                if (t.Port != 22) sb.Append(", \"port\": ").Append(t.Port);
                if (t.Token.Length > 0) sb.Append(", \"token\": ").Append(Js(t.Token));
                if (t.KeyFile.Length > 0) sb.Append(", \"keyFile\": ").Append(Js(t.KeyFile));
                if (t.KnownHosts.Length > 0)
                {
                    string rel = t.KnownHosts;
                    if (rel.StartsWith(Dir, StringComparison.OrdinalIgnoreCase))
                        rel = rel.Substring(Dir.Length).TrimStart('\\');
                    sb.Append(", \"knownHosts\": ").Append(Js(rel));
                }
                if (!t.Enabled) sb.Append(", \"enabled\": false");
                if (t.IdleIntervalSec > 0) sb.Append(", \"idleIntervalSec\": ").Append(t.IdleIntervalSec);
                if (t.RemoteDir.Length > 0) sb.Append(", \"remoteDir\": ").Append(Js(t.RemoteDir));
                if (t.Password.Length > 0) sb.Append(", \"passwordProtected\": ").Append(Js(Protect(t.Password)));
                sb.Append(", \"intervalSec\": ").Append(t.IntervalSec);
                sb.Append(" }");
                if (i < targets.Count - 1) sb.Append(",");
                sb.Append("\n");
            }
            sb.Append("  ]\n}\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        static string Js(string s)
        {
            if (s == null) s = "";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append("\"");
            return sb.ToString();
        }

        public static string Protect(string plain)
        {
            try
            {
                byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(enc);
            }
            catch { return ""; }
        }

        public static string Unprotect(string b64)
        {
            try
            {
                byte[] dec = ProtectedData.Unprotect(Convert.FromBase64String(b64), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(dec);
            }
            catch { return ""; }
        }

        static string Str(IDictionary<string, object> d, string key)
        {
            object o;
            if (d.TryGetValue(key, out o) && o != null) return Convert.ToString(o);
            return "";
        }

        static double Num(IDictionary<string, object> d, string key, double def)
        {
            object o;
            if (d.TryGetValue(key, out o) && o != null)
            {
                try { return Convert.ToDouble(o); }
                catch { return def; }
            }
            return def;
        }
    }
}
