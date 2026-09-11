using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;

// 注意：本文件必须保持 C# 5 兼容。
// 编译机是 .NET Framework 内置 csc.exe，只支持到 C# 5：
//   禁止 $"" 插值、禁止 ?. / ?[]、禁止表达式体成员、禁止 nameof、禁止自动属性初始化器。
namespace ClaudeCodeSetup
{
    internal static class Const
    {
        internal const string ProductName  = "Claude Code";
        internal const string Publisher    = "Anthropic PBC";
        internal const string AppId        = "ClaudeCode";
        internal const string InstallerVer = "1.0.0";

        // 载荷尾部 trailer：前 8 字节为载荷长度(Int64 LE)，后 16 字节为 MAGIC
        internal const string PayloadMagic = "CLDCDSETUP-TRAIL";
        internal const int    TrailerSize  = 24;

        // 官方 v2.1.268 载荷解压后 211MB，预留临时空间
        internal const long RequiredFreeBytes = 700L * 1024 * 1024;

        internal const string ReleaseRepo = "https://github.com/anthropics/claude-code";
        internal const string GitDownloadUrl = "https://git-scm.com/download/win";

        internal const string RegUninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ClaudeCode";
    }

    // ─────────────────────────────────────────────────────────────
    // 构建期注入的信息（由 build.bat 生成 BuildInfo.cs 覆盖）
    // ─────────────────────────────────────────────────────────────

    internal static class Log
    {
        private static readonly object _gate = new object();
        private static readonly List<string> _lines = new List<string>();
        private static string _path;

        internal static void Init(string path) { _path = path; }

        internal static void Write(string line)
        {
            string stamped = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line;
            lock (_gate) { _lines.Add(stamped); }
            try { if (_path != null) File.AppendAllText(_path, stamped + Environment.NewLine, Encoding.UTF8); }
            catch { /* 日志失败绝不阻断安装 */ }
        }

        internal static void Write(string format, params object[] args)
        {
            Write(string.Format(CultureInfo.InvariantCulture, format, args));
        }

        internal static string All()
        {
            lock (_gate) { return string.Join(Environment.NewLine, _lines.ToArray()); }
        }

        internal static List<string> Lines()
        {
            lock (_gate) { return new List<string>(_lines); }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 只读子流：让 ZipArchive 能直接读取「附加在 exe 尾部」的载荷，
    // 避免把 95MB 载荷先复制成临时文件。
    // ─────────────────────────────────────────────────────────────

    internal sealed class SubStream : Stream
    {
        private readonly Stream _base;
        private readonly long _start;
        private readonly long _length;
        private long _pos;

        internal SubStream(Stream baseStream, long start, long length)
        {
            _base = baseStream;
            _start = start;
            _length = length;
            _pos = 0;
        }

        public override bool CanRead  { get { return true; } }
        public override bool CanSeek  { get { return true; } }
        public override bool CanWrite { get { return false; } }
        public override long Length   { get { return _length; } }

        public override long Position
        {
            get { return _pos; }
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException("value");
                _pos = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _length) return 0;
            long remaining = _length - _pos;
            if (count > remaining) count = (int)remaining;
            _base.Seek(_start + _pos, SeekOrigin.Begin);
            int n = _base.Read(buffer, offset, count);
            if (n > 0) _pos += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long t;
            switch (origin)
            {
                case SeekOrigin.Begin:   t = offset; break;
                case SeekOrigin.Current: t = _pos + offset; break;
                default:                 t = _length + offset; break;
            }
            if (t < 0) t = 0;
            _pos = t;
            return _pos;
        }

        public override void Flush() { }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }

    // ─────────────────────────────────────────────────────────────
    // 载荷定位
    // ─────────────────────────────────────────────────────────────

    internal sealed class PayloadRef
    {
        internal long Offset;
        internal long Length;
        internal bool Found;
    }

    internal static class Payload
    {
        /// <summary>从自身 exe 尾部定位附加的载荷 zip。</summary>
        internal static PayloadRef Locate(string selfPath)
        {
            PayloadRef r = new PayloadRef();
            using (FileStream fs = new FileStream(selfPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                long fileLen = fs.Length;
                if (fileLen < Const.TrailerSize) return r;

                byte[] tail = new byte[Const.TrailerSize];
                fs.Seek(fileLen - Const.TrailerSize, SeekOrigin.Begin);
                if (!ReadFully(fs, tail, Const.TrailerSize)) return r;

                string magic = Encoding.ASCII.GetString(tail, 8, 16);
                if (magic != Const.PayloadMagic) return r;

                long len = BitConverter.ToInt64(tail, 0);
                if (len <= 0 || len > fileLen - Const.TrailerSize) return r;

                r.Length = len;
                r.Offset = fileLen - Const.TrailerSize - len;
                r.Found = true;
                return r;
            }
        }

        /// <summary>打开载荷 zip。</summary>
        internal static ZipArchive Open(string selfPath, PayloadRef p)
        {
            FileStream fs = new FileStream(selfPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            SubStream sub = new SubStream(fs, p.Offset, p.Length);
            return new ZipArchive(sub, ZipArchiveMode.Read, false, null);
        }

        internal static bool ReadFully(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 哈希 / 工具
    // ─────────────────────────────────────────────────────────────

    internal static class Util
    {
        internal static string Sha256File(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(h.Length * 2);
                for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        internal static string Sha256File(string path, ProgressSink progress, long total)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] buf = new byte[1024 * 1024];
                long done = 0;
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    sha.TransformBlock(buf, 0, n, null, 0);
                    done += n;
                    if (progress != null && total > 0)
                        progress((int)(done * 100 / total), "校验中");
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                byte[] h = sha.Hash;
                StringBuilder sb = new StringBuilder(h.Length * 2);
                for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>取本机真实架构（考虑 32 位进程跑在 64 位系统上的 WOW64 情形）。</summary>
        internal static string NativeArch()
        {
            string a = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432");
            if (string.IsNullOrEmpty(a)) a = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
            if (string.IsNullOrEmpty(a)) a = "";
            return a.ToUpperInvariant();
        }

        internal static string InstallDir()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(Path.Combine(Path.Combine(local, "Programs"), "ClaudeCode"), "");
        }

        internal static string DesktopDir()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }

        internal static string StartMenuDir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Const.ProductName);
        }

        internal static string ReadString(RegistryKey key, string name)
        {
            if (key == null) return null;
            object o = key.GetValue(name);
            if (o == null) return null;
            return o.ToString();
        }

        /// <summary>在 PATH 风格字符串中查找某个目录（大小写不敏感，忽略尾部反斜杠）。</summary>
        internal static bool PathContains(string pathValue, string dir)
        {
            if (string.IsNullOrEmpty(pathValue)) return false;
            string want = NormalizeDir(dir);
            string[] parts = pathValue.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                if (NormalizeDir(parts[i]) == want) return true;
            }
            return false;
        }

        internal static string NormalizeDir(string d)
        {
            if (d == null) return "";
            string s = d.Trim().Trim('"');
            while (s.Length > 0 && (s[s.Length - 1] == '\\' || s[s.Length - 1] == '/'))
                s = s.Substring(0, s.Length - 1);
            return s.ToLowerInvariant();
        }

        /// <summary>把 dir 前置进 PATH 风格字符串，返回新值；已存在则原样返回。</summary>
        internal static string PrependPath(string pathValue, string dir)
        {
            if (PathContains(pathValue, dir)) return pathValue;
            if (string.IsNullOrEmpty(pathValue)) return dir;
            string trimmed = pathValue.TrimStart(';');
            return dir + ";" + trimmed;
        }

        internal static void BroadcastEnvironmentChange()
        {
            IntPtr result;
            // WM_SETTINGCHANGE = 0x1A，SMTO_ABORTIFHUNG = 0x0002
            SendMessageTimeout(new IntPtr(0xffff), 0x1A, IntPtr.Zero, "Environment",
                               0x0002, 3000, out result);
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam,
            string lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        /// <summary>在注册表中寻找 Git for Windows 安装根目录。</summary>
        internal static List<string> GitRootCandidates()
        {
            List<string> list = new List<string>();

            AddGitRootFromRegistry(list, RegistryHive.LocalMachine, RegistryView.Registry64);
            AddGitRootFromRegistry(list, RegistryHive.LocalMachine, RegistryView.Registry32);
            AddGitRootFromRegistry(list, RegistryHive.CurrentUser,  RegistryView.Default);

            string pf   = Environment.GetEnvironmentVariable("ProgramFiles");
            string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            string lad  = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            if (!string.IsNullOrEmpty(pf))   list.Add(Path.Combine(pf, "Git"));
            if (!string.IsNullOrEmpty(pf86)) list.Add(Path.Combine(pf86, "Git"));
            if (!string.IsNullOrEmpty(lad))  list.Add(Path.Combine(Path.Combine(lad, "Programs"), "Git"));

            // 从 PATH 里反查：<root>\cmd\git.exe -> <root>
            string pathVar = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathVar))
            {
                string[] parts = pathVar.Split(';');
                for (int i = 0; i < parts.Length; i++)
                {
                    string p = parts[i].Trim().Trim('"');
                    if (p.Length == 0) continue;
                    if (p.EndsWith("\\cmd", StringComparison.OrdinalIgnoreCase) ||
                        p.EndsWith("\\bin", StringComparison.OrdinalIgnoreCase) ||
                        p.EndsWith("\\mingw64\\bin", StringComparison.OrdinalIgnoreCase))
                    {
                        string root = p;
                        int cut = root.LastIndexOf('\\');
                        if (cut > 0) root = root.Substring(0, cut);
                        if (root.EndsWith("\\mingw64", StringComparison.OrdinalIgnoreCase))
                            root = root.Substring(0, root.LastIndexOf('\\'));
                        list.Add(root);
                    }
                }
            }
            return list;
        }

        private static void AddGitRootFromRegistry(List<string> list, RegistryHive hive, RegistryView view)
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey k = baseKey.OpenSubKey(@"SOFTWARE\GitForWindows"))
                {
                    string ip = ReadString(k, "InstallPath");
                    if (!string.IsNullOrEmpty(ip)) list.Add(ip.TrimEnd('\\'));
                }
            }
            catch { }
        }

        /// <summary>返回可用的 Git Bash 路径，找不到返回 null。</summary>
        internal static string FindGitBash()
        {
            List<string> roots = GitRootCandidates();
            for (int i = 0; i < roots.Count; i++)
            {
                string root = roots[i];
                if (string.IsNullOrEmpty(root)) continue;

                string[] tries = new string[]
                {
                    Path.Combine(Path.Combine(root, "bin"),     "bash.exe"),
                    Path.Combine(Path.Combine(root, "usr"), "bin", "bash.exe"),
                    Path.Combine(root, "bash.exe")
                };
                for (int j = 0; j < tries.Length; j++)
                {
                    try { if (File.Exists(tries[j])) return tries[j]; }
                    catch { }
                }
            }
            return null;
        }

        internal static string FindOnPath(string exeName)
        {
            string pathVar = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathVar)) return null;
            string[] parts = pathVar.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim().Trim('"');
                if (p.Length == 0) continue;
                try
                {
                    string full = Path.Combine(p, exeName);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
            return null;
        }

        internal static string FindWindowsTerminal()
        {
            string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string p = Path.Combine(Path.Combine(Path.Combine(Path.Combine(lad, "Microsoft"),
                        "WindowsApps"), "wt.exe"), "");
            try { if (File.Exists(p)) return p; } catch { }
            return FindOnPath("wt.exe");
        }
    }

    internal delegate void ProgressSink(int percent, string status);
    internal delegate void LogSink(string line);

    // ─────────────────────────────────────────────────────────────
    // 快捷方式创建（走 WScript.Shell COM 晚绑定，免去手写 .lnk 二进制）
    // ─────────────────────────────────────────────────────────────

    internal static class Shortcut
    {
        internal static void Create(string lnkPath, string targetPath, string arguments,
                                    string workingDir, string description, string iconPath)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("WScript.Shell 不可用，无法创建快捷方式。");

            object shell = Activator.CreateInstance(shellType);
            try
            {
                object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                    null, shell, new object[] { lnkPath });

                Type scType = sc.GetType();
                Set(scType, sc, "TargetPath",        targetPath);
                Set(scType, sc, "Arguments",         arguments);
                Set(scType, sc, "WorkingDirectory",  workingDir);
                Set(scType, sc, "Description",       description);
                if (!string.IsNullOrEmpty(iconPath)) Set(scType, sc, "IconLocation", iconPath);
                scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
            }
            finally
            {
                Marshal.ReleaseComObject(shell);
            }
        }

        private static void Set(Type t, object obj, string prop, string value)
        {
            t.InvokeMember(prop, BindingFlags.SetProperty, null, obj, new object[] { value });
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 网络探测（放在 Common.cs 以便卸载器也能复用）
    // ─────────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────
    // 网络探测
    // ─────────────────────────────────────────────────────────────

    internal sealed class NetProbe
    {
        internal string Host = "";
        internal bool TcpOk;
        internal bool TlsOk;
        internal bool CertValid;
        internal long TcpMs = -1;
        internal long TlsMs = -1;
        internal string Error = "";

        internal int Score()
        {
            if (TlsOk) return 2;
            if (TcpOk) return 1;
            return 0;
        }

        internal string Summary()
        {
            if (TlsOk)
            {
                string cert = CertValid ? "证书有效" : "证书未通过校验";
                return string.Format(CultureInfo.InvariantCulture,
                    "{0}  TCP {1}ms  TLS {2}ms  ({3})", Host, TcpMs, TlsMs, cert);
            }
            if (TcpOk) return string.Format(CultureInfo.InvariantCulture, "{0}  TCP 通，但 TLS 握手失败：{1}", Host, Error);
            return string.Format(CultureInfo.InvariantCulture, "{0}  不可达：{1}", Host, Error);
        }
    }

    internal static class Net
    {
        internal static void ConfigureTls()
        {
            // 这里只影响 HttpWebRequest。注意两个坑：
            //   1) 手动 OR 上 0x3000(TLS1.3) —— 运行时没有该枚举值，会以「函数不受支持」失败；
            //   2) SystemDefault(0) —— ServicePointManager 的属性校验会拒绝，报「枚举中无效」。
            // 因此显式使用 Tls12；SslStream 一侧另有独立协议参数，不受此处影响。
            try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }
            catch { }
        }

        internal static NetProbe Probe(string host, int timeoutMs)
        {
            NetProbe p = new NetProbe();
            p.Host = host;

            // 1) DNS
            try
            {
                IPAddress[] addrs = Dns.GetHostAddresses(host);
                if (addrs == null || addrs.Length == 0)
                {
                    p.Error = "DNS 无解析结果";
                    return p;
                }
            }
            catch (Exception ex)
            {
                p.Error = "DNS 解析失败(" + ex.Message + ")";
                return p;
            }

            // 2) TCP
            Stopwatch sw = Stopwatch.StartNew();
            TcpClient client = new TcpClient();
            try
            {
                IAsyncResult ar = client.BeginConnect(host, 443, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false))
                {
                    p.Error = "TCP 连接超时";
                    try { client.Close(); } catch { }
                    return p;
                }
                client.EndConnect(ar);
                sw.Stop();
                p.TcpOk = true;
                p.TcpMs = sw.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                p.Error = "TCP 连接失败(" + Root(ex) + ")";
                try { client.Close(); } catch { }
                return p;
            }

            // 3) TLS
            // 关键：SslStream.AuthenticateAsClient(host) 在不显式传协议时只会用
            // SslProtocols.Default（SSL3 + TLS1.0），现代服务器一律拒绝，表现为
            // 「要求的函数不受支持」。必须显式给出协议集，因此这里逐个候选重试。
            string lastErr = "";
            SslProtocols[] candidates = new SslProtocols[]
            {
                (SslProtocols)0,                   // None = 由 Schannel 决定，可协商到 TLS 1.2/1.3
                SslProtocols.Tls12                 // 老运行时兜底
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                long ms;
                bool certValid;
                string err;
                if (TryHandshake(host, 443, timeoutMs, candidates[i], out ms, out certValid, out err))
                {
                    p.TlsOk = true;
                    p.TlsMs = ms;
                    p.CertValid = certValid;
                    try { client.Close(); } catch { }
                    return p;
                }
                lastErr = err;
            }

            try { client.Close(); } catch { }
            p.Error = "TLS 握手失败(" + lastErr + ")";
            return p;
        }

        /// <summary>
        /// 建立 TCP 并完成 TLS 握手。SslStream.AuthenticateAsClient 本身没有超时参数，
        /// 对「端口通但握手不回应」的对端会一直挂到系统默认超时（~21s），
        /// 因此这里必须用独立线程 + 看门狗兜底，否则预检会被单个主机拖死。
        /// </summary>
        private static bool TryHandshake(string host, int port, int timeoutMs, SslProtocols protos,
                                         out long ms, out bool certValid, out string err)
        {
            long msLocal = -1;
            bool cvLocal = false;
            string errLocal = "";
            bool okLocal = false;

            ManualResetEvent done = new ManualResetEvent(false);

            Thread worker = new Thread(delegate()
            {
                TcpClient c = new TcpClient();
                try
                {
                    c.ReceiveTimeout = timeoutMs;
                    c.SendTimeout = timeoutMs;

                    IAsyncResult ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false))
                    {
                        errLocal = "连接超时";
                        return;
                    }
                    c.EndConnect(ar);

                    Stopwatch sw = Stopwatch.StartNew();
                    using (SslStream ssl = new SslStream(c.GetStream(), false, ValidateCert))
                    {
                        ssl.ReadTimeout = timeoutMs;
                        ssl.WriteTimeout = timeoutMs;
                        ssl.AuthenticateAsClient(host, null, protos, false);
                        sw.Stop();
                        msLocal = sw.ElapsedMilliseconds;
                        cvLocal = _lastCertValid;
                        okLocal = true;
                    }
                }
                catch (Exception ex) { errLocal = Root(ex); }
                finally
                {
                    try { c.Close(); } catch { }
                    try { done.Set(); } catch { }
                }
            });
            worker.IsBackground = true;
            worker.Start();

            bool finished = done.WaitOne(timeoutMs + 2000, false);
            if (!finished)
            {
                // worker 是后台线程，会被直接回收，不会拖住进程退出
                ms = -1; certValid = false;
                err = "TLS 握手超时（>" + (timeoutMs + 2000) + "ms）";
                return false;
            }

            ms = msLocal;
            certValid = cvLocal;
            err = errLocal;
            return okLocal;
        }

        private static volatile bool _lastCertValid;

        private static bool ValidateCert(object sender, System.Security.Cryptography.X509Certificates.X509Certificate cert,
                                         System.Security.Cryptography.X509Certificates.X509Chain chain,
                                         SslPolicyErrors errors)
        {
            _lastCertValid = (errors == SslPolicyErrors.None);
            // 仍放行，以便完成握手并区分「连不通」与「证书被拦截」
            return true;
        }

        internal static string Root(Exception ex)
        {
            Exception e = ex;
            while (e.InnerException != null) e = e.InnerException;
            return e.Message;
        }

        /// <summary>对 api.anthropic.com 做一次真正的 HTTPS 请求，端到端确认 API 边缘可达。</summary>
        internal static string ProbeApiHttp(int timeoutMs)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://api.anthropic.com/v1/messages");
                req.Method = "HEAD";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.AllowAutoRedirect = false;
                req.UserAgent = "ClaudeCodeSetup/" + Const.InstallerVer;

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    return "HTTP " + (int)resp.StatusCode;
                }
            }
            catch (WebException wex)
            {
                if (wex.Response != null)
                {
                    try
                    {
                        using (HttpWebResponse r = (HttpWebResponse)wex.Response)
                            return "HTTP " + (int)r.StatusCode;
                    }
                    catch { }
                }
                return "请求失败：" + Root(wex);
            }
            catch (Exception ex)
            {
                return "请求失败：" + Root(ex);
            }
        }

        /// <summary>
        /// 断点无关的整文件下载，带进度与实时速率。
        /// 用于 ARM64 机器按需拉取官方载荷（x64 走内置载荷，不走这里）。
        /// </summary>
        internal static bool DownloadFile(string url, string dest, ProgressSink progress, out string error)
        {
            error = "";
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = 30000;
                req.ReadWriteTimeout = 60000;
                req.AllowAutoRedirect = true;
                req.UserAgent = "ClaudeCodeSetup/" + Const.InstallerVer;

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    if ((int)resp.StatusCode != 200)
                    {
                        error = "服务器返回 HTTP " + (int)resp.StatusCode;
                        return false;
                    }
                    long total = resp.ContentLength;
                    using (Stream input = resp.GetResponseStream())
                    using (FileStream output = new FileStream(dest, FileMode.Create, FileAccess.Write,
                                                              FileShare.None, 1024 * 1024))
                    {
                        byte[] buf = new byte[1024 * 1024];
                        long done = 0;
                        int n;
                        int lastPct = -1;
                        Stopwatch sw = Stopwatch.StartNew();
                        while ((n = input.Read(buf, 0, buf.Length)) > 0)
                        {
                            output.Write(buf, 0, n);
                            done += n;
                            if (progress != null && total > 0)
                            {
                                int pct = 5 + (int)(done * 45 / total);
                                if (pct != lastPct)
                                {
                                    lastPct = pct;
                                    double mb = done / 1048576.0;
                                    double tmb = total / 1048576.0;
                                    double secs = sw.Elapsed.TotalSeconds;
                                    double rate = secs > 0.5 ? mb / secs : 0;
                                    progress(pct, string.Format(CultureInfo.InvariantCulture,
                                        "下载中… {0:F0}% ({1:F0}/{2:F0} MB)  {3:F2} MB/s",
                                        done * 100.0 / total, mb, tmb, rate));
                                }
                            }
                        }
                        output.Flush();
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = Root(ex);
                return false;
            }
        }
    }

}
