using System;
using System.IO;
using System.Text;

namespace Gpm
{
    // Only callers' explicitly named diagnostics are managed; configuration and exports are untouched.
    public static class LocalFiles
    {
        static readonly object Gate=new object();
        static readonly Encoding Utf8=new UTF8Encoding(false);
        public static void Append(string path,string text,int limit)
        {
            try {lock(Gate){
                if(text.Length>limit/4)text=text.Substring(0,limit/4)+"\n[truncated]\n";
                if(File.Exists(path)&&new FileInfo(path).Length+Utf8.GetByteCount(text)>limit)
                {
                    string old=path+".1";if(File.Exists(old))File.Delete(old);File.Move(path,old);Trim(old,limit);
                }
                File.AppendAllText(path,text,Utf8);
            }}catch{}
        }
        static void Trim(string path,int limit)
        {
            if(new FileInfo(path).Length<=limit)return;
            string tail;using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
            {stream.Seek(-limit/2,SeekOrigin.End);using(var reader=new StreamReader(stream,Utf8)){reader.ReadLine();tail=reader.ReadToEnd();}}
            File.WriteAllText(path,tail,Utf8);
        }
        public static void WriteDiagnostic(string path,string text)
        {
            try{lock(Gate){if(text.Length>8000)text=text.Substring(0,8000)+"\n[truncated]";File.WriteAllText(path,text,Utf8);}}catch{}
        }
        public static void Cleanup(string dir,DateTime utc)
        {
            // Fixed allowlist: never enumerate or delete arbitrary files in the configured data directory.
            foreach(string name in new[]{"runtime.log","ui.log","probe.log","probe-tray.log"})
                foreach(string suffix in new[]{"",".1"})
                    try{string path=Path.Combine(dir,name+suffix);if(File.Exists(path)){if(utc-File.GetLastWriteTimeUtc(path)>TimeSpan.FromDays(7))File.Delete(path);else Trim(path,name=="runtime.log"?262144:65536);}}catch{}
            foreach(string name in new[]{"ssh-error.txt","startup-error.txt"})
                try{string path=Path.Combine(dir,name);if(File.Exists(path)&&utc-File.GetLastWriteTimeUtc(path)>TimeSpan.FromDays(7))File.Delete(path);}catch{}
        }
    }
}
