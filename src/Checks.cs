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
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;

// C# 5 兼容
namespace ClaudeCodeSetup
{
    internal enum CheckState { Pending, Running, Pass, Warn, Fail, Info }

    internal sealed class CheckItem
    {
        internal string Id;
        internal string Title;
        internal CheckState State = CheckState.Pending;
        internal string Detail = "";
        internal string Advice = "";

        internal CheckItem(string id, string title)
        {
            Id = id;
            Title = title;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 预检执行器
    // ─────────────────────────────────────────────────────────────

    internal sealed class PreflightResult
    {
        internal List<CheckItem> Items = new List<CheckItem>();
        internal bool HasBlockingFailure;
        internal bool HasWarning;
        internal string Arch = "";
        internal bool UseBundled;          // true=x64 用内置载荷；false=arm64 需下载
        internal string GitBash;
        internal bool AlreadyInstalled;
        internal NetProbe Api;
        internal NetProbe Github;
        internal string ApiHttp = "";
        internal int ApiScore;
        internal int GithubScore;
        internal long PayloadBytes;
        internal long InstallBytes;

        internal CheckItem Find(string id)
        {
            for (int i = 0; i < Items.Count; i++)
                if (Items[i].Id == id) return Items[i];
            return null;
        }
    }

    internal static class Preflight
    {
        internal static PreflightResult Run(string selfPath, Action<CheckItem> onItem)
        {
            PreflightResult res = new PreflightResult();

            Add(res, onItem, "payload", "安装包完整性", CheckBundledPayload(selfPath, res));
            Add(res, onItem, "os",      "操作系统",     CheckOs());
            Add(res, onItem, "arch",    "CPU 架构",     CheckArch(res));
            Add(res, onItem, "disk",    "磁盘空间",     CheckDisk());
            Add(res, onItem, "net",     "网络连接",     CheckNetwork(res));
            Add(res, onItem, "git",     "Git for Windows", CheckGit(res));
            Add(res, onItem, "existing","已有安装",     CheckExisting(res));
            Add(res, onItem, "running", "进程占用",     CheckRunning());
            Add(res, onItem, "longpath","长路径支持",   CheckLongPaths());
            Add(res, onItem, "defender","杀毒实时防护", CheckDefender());

            for (int i = 0; i < res.Items.Count; i++)
            {
                CheckItem it = res.Items[i];
                if (it.State == CheckState.Fail) res.HasBlockingFailure = true;
                if (it.State == CheckState.Warn) res.HasWarning = true;
            }
            return res;
        }

        private static void Add(PreflightResult res, Action<CheckItem> onItem, string id, string title, CheckItem item)
        {
            item.Id = id;
            item.Title = title;
            res.Items.Add(item);
            if (onItem != null) onItem(item);
        }

        // ── 1. 内置载荷完整性 ────────────────────────────────────
        private static CheckItem CheckBundledPayload(string selfPath, PreflightResult res)
        {
            CheckItem c = new CheckItem("payload", "安装包完整性");
            PayloadRef p = Payload.Locate(selfPath);
            if (!p.Found)
            {
                c.State = CheckState.Fail;
                c.Detail = "未在本程序尾部找到内置载荷";
                c.Advice = "安装包可能已损坏或被截断，请重新下载完整文件。";
                return c;
            }
            res.PayloadBytes = p.Length;

            try
            {
                using (ZipArchive zip = Payload.Open(selfPath, p))
                {
                    ZipArchiveEntry e = zip.GetEntry("claude.exe");
                    if (e == null)
                    {
                        c.State = CheckState.Fail;
                        c.Detail = "载荷中缺少 claude.exe";
                        c.Advice = "载荷结构异常，请重新下载安装包。";
                        return c;
                    }
                    res.InstallBytes = e.Length;
                }
            }
            catch (Exception ex)
            {
                c.State = CheckState.Fail;
                c.Detail = "载荷无法读取：" + Net.Root(ex);
                c.Advice = "安装包可能已损坏，请重新下载。";
                return c;
            }

            // 与官方 SHASUMS256 比对（值由构建期注入 BuildInfo）
            string actual = Sha256Region(selfPath, p.Offset, p.Length);
            if (!string.IsNullOrEmpty(BuildInfo.PayloadSha256) &&
                !string.Equals(actual, BuildInfo.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            {
                c.State = CheckState.Fail;
                c.Detail = "载荷 SHA256 与官方不符";
                c.Advice = string.Format(CultureInfo.InvariantCulture,
                    "期望 {0}…  实际 {1}…  请重新下载安装包。",
                    BuildInfo.PayloadSha256.Substring(0, 16), actual.Substring(0, 16));
                return c;
            }

            c.State = CheckState.Pass;
            c.Detail = string.Format(CultureInfo.InvariantCulture,
                "内置 {0} 载荷 {1} MB，SHA256 与官方一致 ({2}…)",
                BuildInfo.ReleaseTag, p.Length / 1048576, actual.Substring(0, 16));
            return c;
        }

        private static string Sha256Region(string path, long offset, long length)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SubStream sub = new SubStream(fs, offset, length))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(sub);
                StringBuilder sb = new StringBuilder(h.Length * 2);
                for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        // ── 2. 操作系统 ──────────────────────────────────────────
        private static CheckItem CheckOs()
        {
            CheckItem c = new CheckItem("os", "操作系统");
            int build = 0;
            string name = "";
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    string b = Util.ReadString(k, "CurrentBuildNumber");
                    name = Util.ReadString(k, "ProductName");
                    string disp = Util.ReadString(k, "DisplayVersion");
                    if (string.IsNullOrEmpty(disp)) disp = Util.ReadString(k, "ReleaseId");
                    if (!string.IsNullOrEmpty(disp) && !string.IsNullOrEmpty(name))
                        name = name + " " + disp;
                    if (!string.IsNullOrEmpty(b)) int.TryParse(b, out build);
                }
            }
            catch { }

            if (build == 0)
            {
                Version v = Environment.OSVersion.Version;
                build = v.Build;
                name = "Windows (build " + build + ")";
            }

            if (build < 10240)
            {
                c.State = CheckState.Fail;
                c.Detail = name + " (build " + build + ")";
                c.Advice = "Claude Code 需要 Windows 10 1809 (build 17763) 或更高版本。";
                return c;
            }
            if (build < 17763)
            {
                c.State = CheckState.Fail;
                c.Detail = name + " (build " + build + ")";
                c.Advice = "版本过旧，需要 Windows 10 1809 (build 17763) 或更高版本。";
                return c;
            }

            string label = build >= 22000 ? "Windows 11" : "Windows 10";
            if (name.IndexOf("Windows 11", StringComparison.OrdinalIgnoreCase) >= 0) label = "Windows 11";

            c.State = CheckState.Pass;
            c.Detail = string.Format(CultureInfo.InvariantCulture, "{0} (build {1})", label, build);
            return c;
        }

        // ── 3. CPU 架构 ──────────────────────────────────────────
        private static CheckItem CheckArch(PreflightResult res)
        {
            CheckItem c = new CheckItem("arch", "CPU 架构");
            string a = Util.NativeArch();
            res.Arch = a;

            if (a == "AMD64" || a == "X86")
            {
                res.UseBundled = true;
                c.State = CheckState.Pass;
                c.Detail = "x64 — 使用内置载荷，无需联网下载";
                return c;
            }
            if (a == "ARM64")
            {
                res.UseBundled = false;
                c.State = CheckState.Info;
                c.Detail = "ARM64 — 需从 GitHub 下载对应版本 (约 96 MB)";
                c.Advice = "安装前请确认网络畅通。";
                return c;
            }

            res.UseBundled = true;
            c.State = CheckState.Warn;
            c.Detail = "未能识别的架构：" + (a.Length == 0 ? "(未知)" : a) + "，将按 x64 处理";
            return c;
        }

        // ── 4. 磁盘空间 ──────────────────────────────────────────
        private static CheckItem CheckDisk()
        {
            CheckItem c = new CheckItem("disk", "磁盘空间");
            try
            {
                string dir = Util.InstallDir();
                string root = Path.GetPathRoot(dir);
                DriveInfo di = new DriveInfo(root);
                long free = di.AvailableFreeSpace;

                string detail = string.Format(CultureInfo.InvariantCulture,
                    "{0} 可用 {1:F1} GB，安装约需 0.3 GB", root.TrimEnd('\\'), free / 1073741824.0);

                if (free < Const.RequiredFreeBytes)
                {
                    c.State = CheckState.Fail;
                    c.Detail = detail;
                    c.Advice = "可用空间不足，请清理磁盘后重试。";
                    return c;
                }
                c.State = CheckState.Pass;
                c.Detail = detail;
            }
            catch (Exception ex)
            {
                c.State = CheckState.Warn;
                c.Detail = "无法读取磁盘信息：" + Net.Root(ex);
            }
            return c;
        }

        // ── 5. 网络连接 ──────────────────────────────────────────
        private static CheckItem CheckNetwork(PreflightResult res)
        {
            CheckItem c = new CheckItem("net", "网络连接");
            Net.ConfigureTls();

            NetProbe[] probes = new NetProbe[2];
            Thread[] threads = new Thread[2];

            threads[0] = new Thread(delegate() { probes[0] = Net.Probe("api.anthropic.com", 5000); });
            threads[1] = new Thread(delegate() { probes[1] = Net.Probe("github.com", 5000); });
            for (int i = 0; i < 2; i++) { threads[i].IsBackground = true; threads[i].Start(); }
            // 两个主机并行探测，每台最多 2 个协议候选，单个候选上限 7s —— 最坏 13s 左右。
            // 网络正常时首个候选即成功，实测 <1s。
            for (int i = 0; i < 2; i++) threads[i].Join(13000);

            if (probes[0] == null) probes[0] = new NetProbe { Host = "api.anthropic.com", Error = "探测超时" };
            if (probes[1] == null) probes[1] = new NetProbe { Host = "github.com",       Error = "探测超时" };

            res.Api = probes[0];
            res.Github = probes[1];
            res.ApiScore = probes[0].Score();
            res.GithubScore = probes[1].Score();

            string apiLine = "api.anthropic.com: " + probes[0].Summary();
            string ghLine  = "github.com: " + probes[1].Summary()
                           + (res.UseBundled ? "   （仅 ARM64 版需要，本机可忽略）" : "");

            // TLS 通了才有意义做端到端 HTTP 探测：确认 API 边缘真的在应答，
            // 而不只是「某个 TLS 端口开着」（代理/透明劫持会伪装成前者）。
            if (probes[0].TlsOk)
            {
                string http = Net.ProbeApiHttp(8000);
                res.ApiHttp = http;
                apiLine += "\n    HTTPS 请求: " + http;
            }

            // ARM64 走在线下载，网络不可用即为阻断性失败
            if (!res.UseBundled)
            {
                if (probes[1].Score() < 2)
                {
                    c.State = CheckState.Fail;
                    c.Detail = apiLine + "\n" + ghLine;
                    c.Advice = "ARM64 版需从 GitHub 下载，当前无法连接，安装会失败。请检查网络或代理后重试。";
                    return c;
                }
                c.State = CheckState.Pass;
                c.Detail = apiLine + "\n" + ghLine;
                return c;
            }

            if (probes[0].Score() == 2)
            {
                c.State = CheckState.Pass;
                c.Detail = apiLine + "\n" + ghLine;
                return c;
            }

            if (probes[0].Score() == 1)
            {
                c.State = CheckState.Warn;
                c.Detail = apiLine + "\n" + ghLine;
                c.Advice = "TLS 握手异常，可能存在代理或证书拦截。安装本身不受影响（载荷已内置），"
                         + "但 Claude Code 运行时需要能访问 api.anthropic.com。";
                return c;
            }

            c.State = CheckState.Warn;
            c.Detail = apiLine + "\n" + ghLine;
            c.Advice = "安装本身不需要联网（二进制已内置），可以继续；"
                     + "但安装后 Claude Code 必须联网才能使用，请确认网络或代理设置。";
            return c;
        }

        // ── 6. Git for Windows ───────────────────────────────────
        private static CheckItem CheckGit(PreflightResult res)
        {
            CheckItem c = new CheckItem("git", "Git for Windows");
            string bash = Util.FindGitBash();
            res.GitBash = bash;

            if (!string.IsNullOrEmpty(bash))
            {
                c.State = CheckState.Pass;
                c.Detail = "已找到 Git Bash：" + bash;
                return c;
            }

            c.State = CheckState.Warn;
            c.Detail = "未检测到 Git for Windows";
            c.Advice = "Claude Code 在 Windows 上依赖 Git Bash 执行命令与工具调用。\n"
                     + "安装后请执行以下任一操作补齐：\n"
                     + "    • 命令行运行：winget install Git.Git\n"
                     + "    • 或访问：" + Const.GitDownloadUrl + "\n"
                     + "本次安装不会代替你安装第三方软件。";
            return c;
        }

        // ── 7. 已有安装 ──────────────────────────────────────────
        private static CheckItem CheckExisting(PreflightResult res)
        {
            CheckItem c = new CheckItem("existing", "已有安装");

            string dir = Util.InstallDir();
            string exe = Path.Combine(dir, "claude.exe");
            string found = null;

            if (File.Exists(exe))
            {
                found = "本安装包目录 (" + dir + ")";
                res.AlreadyInstalled = true;
            }
            else
            {
                string onPath = Util.FindOnPath("claude.exe");
                if (string.IsNullOrEmpty(onPath)) onPath = Util.FindOnPath("claude.cmd");
                if (!string.IsNullOrEmpty(onPath))
                {
                    found = "PATH 中已存在：" + onPath;
                    res.AlreadyInstalled = true;
                }
                else
                {
                    string npm = Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.ApplicationData), @"npm\claude.cmd");
                    if (File.Exists(npm))
                    {
                        found = "npm 全局安装：" + npm;
                        res.AlreadyInstalled = true;
                    }
                }
            }

            if (found == null)
            {
                c.State = CheckState.Pass;
                c.Detail = "未发现已有安装，将执行全新安装";
                return c;
            }

            c.State = CheckState.Warn;
            c.Detail = found;
            c.Advice = "检测到已有 Claude Code。继续安装将把原生版本装到 " + dir + " 并置入 PATH 末尾，"
                     + "不会删除原有安装。如需避免冲突，可先卸载旧版本。";
            return c;
        }

        // ── 8. 进程占用 ──────────────────────────────────────────
        private static CheckItem CheckRunning()
        {
            CheckItem c = new CheckItem("running", "进程占用");
            try
            {
                Process[] ps = Process.GetProcessesByName("claude");
                if (ps.Length > 0)
                {
                    c.State = CheckState.Warn;
                    c.Detail = "检测到 " + ps.Length + " 个 claude 进程正在运行";
                    c.Advice = "建议先关闭所有正在运行的 Claude Code 窗口，否则覆盖文件可能失败。";
                    return c;
                }
            }
            catch { }
            c.State = CheckState.Pass;
            c.Detail = "无 claude 进程占用";
            return c;
        }

        // ── 9. 长路径支持 ────────────────────────────────────────
        private static CheckItem CheckLongPaths()
        {
            CheckItem c = new CheckItem("longpath", "长路径支持");
            int lp = 0;
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\FileSystem"))
                {
                    object o = k == null ? null : k.GetValue("LongPathsEnabled");
                    if (o != null) int.TryParse(o.ToString(), out lp);
                }
            }
            catch { }

            if (lp == 1)
            {
                c.State = CheckState.Pass;
                c.Detail = "已启用 (LongPathsEnabled=1)";
                return c;
            }
            c.State = CheckState.Info;
            c.Detail = "未启用";
            c.Advice = "一般不影响使用。若日后遇到超长路径报错，可用管理员权限执行：\n"
                     + "    reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\FileSystem\" /v LongPathsEnabled /t REG_DWORD /d 1 /f";
            return c;
        }

        // ── 10. 杀毒实时防护 ─────────────────────────────────────
        private static CheckItem CheckDefender()
        {
            CheckItem c = new CheckItem("defender", "杀毒实时防护");
            try
            {
                bool defenderPresent = false;
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows Defender"))
                    defenderPresent = (k != null);

                if (!defenderPresent)
                {
                    c.State = CheckState.Info;
                    c.Detail = "未检测到 Windows Defender";
                    return c;
                }

                c.State = CheckState.Info;
                c.Detail = "Windows Defender 存在，解压大文件时可能被扫描而变慢";
                c.Advice = "属正常现象。若解压阶段明显卡顿，可临时为该安装目录添加排除项。";
            }
            catch
            {
                c.State = CheckState.Info;
                c.Detail = "无法读取防护状态";
            }
            return c;
        }
    }
}
