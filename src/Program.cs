using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// C# 5 兼容
namespace ClaudeCodeSetup
{
    internal static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);
        private const int ATTACH_PARENT_PROCESS = -1;

        private static readonly StringBuilder _report = new StringBuilder();
        private static string _reportPath;

        [STAThread]
        internal static int Main(string[] args)
        {
            Net.ConfigureTls();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string selfPath = Application.ExecutablePath;
            _reportPath = ArgValue(args, "--report=");

            bool selftest  = HasFlag(args, "--selftest");
            bool sInstall  = HasFlag(args, "--silent-install");
            bool sUninst   = HasFlag(args, "--silent-uninstall");

            if (selftest || sInstall || sUninst)
            {
                AttachConsole(ATTACH_PARENT_PROCESS);
                try
                {
                    StreamWriter so = new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8);
                    so.AutoFlush = true;
                    Console.SetOut(so);
                }
                catch { }
                try { Console.OutputEncoding = Encoding.UTF8; } catch { }

                int code;
                if (sUninst)     code = RunSilentUninstall(args);
                else if (sInstall) code = RunSilentInstall(selfPath, args);
                else             code = RunSelfTest(selfPath);
                return code;
            }

            // ── GUI 模式 ────────────────────────────────────────
            string tempLog = Path.Combine(Path.GetTempPath(),
                "ClaudeCodeSetup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");
            Log.Init(tempLog);
            Log.Write("Claude Code 安装程序 v" + Const.InstallerVer + " 启动");
            Log.Write("自身路径: " + selfPath);
            Log.Write("内置版本: " + BuildInfo.ReleaseTag + " (" + BuildInfo.AssetName + ")");

            Application.Run(new MainForm(selfPath));
            return 0;
        }

        // ─────────────────────────────────────────────────────────
        // --selftest ：只跑预检并打印，不做任何改动
        // ─────────────────────────────────────────────────────────
        private static int RunSelfTest(string selfPath)
        {
            Out("===== Claude Code 安装程序 自检 =====");
            Out("构建版本   : " + Const.InstallerVer);
            Out("目标版本   : " + BuildInfo.ReleaseVersion + "  (" + BuildInfo.ReleaseTag + ")");
            Out("载荷资源   : " + BuildInfo.AssetName);
            Out("载荷 SHA256: " + BuildInfo.PayloadSha256);
            Out("二进制 SHA : " + BuildInfo.BinarySha256);
            Out("");

            DateTime t0 = DateTime.Now;
            PreflightResult r = Preflight.Run(selfPath, null);
            double secs = (DateTime.Now - t0).TotalSeconds;

            Out(string.Format(CultureInfo.InvariantCulture, "----- 预检结果（耗时 {0:F2}s）-----", secs));
            int fail = 0, warn = 0, pass = 0, info = 0;
            for (int i = 0; i < r.Items.Count; i++)
            {
                CheckItem it = r.Items[i];
                string tag;
                switch (it.State)
                {
                    case CheckState.Pass: tag = "[PASS]"; pass++; break;
                    case CheckState.Warn: tag = "[WARN]"; warn++; break;
                    case CheckState.Fail: tag = "[FAIL]"; fail++; break;
                    default:              tag = "[INFO]"; info++; break;
                }
                Out(tag + " " + it.Title);
                if (it.Detail.Length > 0)
                    foreach (string l in it.Detail.Replace("\r\n", "\n").Split('\n')) Out("        " + l);
                if (it.Advice.Length > 0)
                    foreach (string l in it.Advice.Replace("\r\n", "\n").Split('\n')) Out("        > " + l);
            }

            Out("");
            Out(string.Format(CultureInfo.InvariantCulture,
                "汇总: 通过 {0} / 提示 {1} / 警告 {2} / 不通过 {3}", pass, info, warn, fail));
            Out("架构: " + r.Arch + "   使用内置载荷: " + (r.UseBundled ? "是" : "否"));
            Out("Git Bash: " + (string.IsNullOrEmpty(r.GitBash) ? "(未找到)" : r.GitBash));
            Out("已安装: " + (r.AlreadyInstalled ? "是" : "否"));
            if (r.Api != null)    Out("API 探测: " + r.Api.Summary());
            if (r.Github != null) Out("GitHub 探测: " + r.Github.Summary());
            if (!string.IsNullOrEmpty(r.ApiHttp)) Out("API 端到端: " + r.ApiHttp);

            bool ok = !r.HasBlockingFailure;
            Out("");
            Out(ok ? "结论: 环境可用于安装。" : "结论: 存在阻断性问题，安装会被拒绝。");
            Flush();
            return ok ? 0 : 1;
        }

        // ─────────────────────────────────────────────────────────
        // --silent-install ：无界面安装（用于自动化端到端测试）
        // ─────────────────────────────────────────────────────────
        private static int RunSilentInstall(string selfPath, string[] args)
        {
            string target = ArgValue(args, "--target-dir=");
            bool noShortcuts = HasFlag(args, "--no-shortcuts");
            bool noPath = HasFlag(args, "--no-path");
            // 强制走在线下载路径，用于在 x64 机器上验证 ARM64 下载逻辑
            bool forceDownload = HasFlag(args, "--force-download");

            Out("===== 静默安装 =====");
            PreflightResult pre = Preflight.Run(selfPath, null);
            Out("预检: 通过 " + CountPass(pre) + " 项，阻断性失败: " + (pre.HasBlockingFailure ? "是" : "否"));
            if (pre.HasBlockingFailure)
            {
                for (int i = 0; i < pre.Items.Count; i++)
                    if (pre.Items[i].State == CheckState.Fail)
                        Out("  [FAIL] " + pre.Items[i].Title + " — " + pre.Items[i].Detail);
                Out("安装中止。");
                Flush();
                return 2;
            }

            InstallOptions opt = new InstallOptions();
            opt.InstallDir = string.IsNullOrEmpty(target) ? Util.InstallDir() : target;
            opt.GitBash = pre.GitBash;
            opt.UseBundled = forceDownload ? false : pre.UseBundled;
            opt.Arch = pre.Arch;
            bool noSystem = HasFlag(args, "--no-system");
            opt.AddToPath = !noPath && !noSystem;
            opt.CreateDesktopShortcut = !noShortcuts && !noSystem;
            opt.CreateStartMenuShortcut = !noShortcuts && !noSystem;
            opt.SystemChanges = !noSystem;

            Out("安装目录: " + opt.InstallDir);

            DateTime t0 = DateTime.Now;
            _lastPct = -1;
            _lastPrefix = "";
            InstallResult res = InstallerCore.Install(selfPath, opt, ProgressOut, null);
            double secs = (DateTime.Now - t0).TotalSeconds;
            Out(string.Format(CultureInfo.InvariantCulture, "安装耗时: {0:F2}s", secs));

            if (!res.Ok)
            {
                Out("安装失败: " + res.Error);
                Flush();
                return 3;
            }
            Out("安装成功，claude --version => " + res.VersionOutput);

            InstallerCore.Verify(opt, res, null, null);
            Out("");
            Out("----- 完整性验证 -----");
            for (int i = 0; i < res.VerifyLines.Count; i++) Out("  " + res.VerifyLines[i]);
            Out("");
            if (res.VerifyFailures.Count == 0) Out("验证结论: 全部通过");
            else
            {
                Out("验证结论: " + res.VerifyFailures.Count + " 项未通过");
                for (int i = 0; i < res.VerifyFailures.Count; i++) Out("   - " + res.VerifyFailures[i]);
            }
            Flush();
            return res.VerifyFailures.Count == 0 ? 0 : 4;
        }

        // ─────────────────────────────────────────────────────────
        // --silent-uninstall
        // ─────────────────────────────────────────────────────────
        private static int RunSilentUninstall(string[] args)
        {
            Out("===== 静默卸载 =====");
            // 默认保留 ~/.claude；要删除必须显式传 --delete-config
            bool keep = !HasFlag(args, "--delete-config");
            Out(keep ? "保留用户配置 ~/.claude" : "警告：将删除用户配置 ~/.claude");
            string err;
            string target = ArgValue(args, "--target-dir=");
            bool ok = InstallerCore.Uninstall(target, null, keep, out err);
            Out(ok ? "卸载完成。" : "卸载失败: " + err);
            Flush();
            return ok ? 0 : 5;
        }

        // ─────────────────────────────────────────────────────────
        private static int CountPass(PreflightResult r)
        {
            int n = 0;
            for (int i = 0; i < r.Items.Count; i++) if (r.Items[i].State == CheckState.Pass) n++;
            return n;
        }

        private static bool HasFlag(string[] args, string flag)
        {
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string ArgValue(string[] args, string prefix)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return args[i].Substring(prefix.Length).Trim('"');
            }
            return null;
        }

        private static int _lastPct = -1;
        private static string _lastPrefix = "";

        /// <summary>
        /// 进度回调每秒会触发几十次。CLI 下按「步骤前缀变化」或「每 10%」节流，
        /// 否则一个 211MB 的解压会刷出几百行。
        /// </summary>
        private static void ProgressOut(int pct, string status)
        {
            string prefix = status;
            int cut = status.IndexOf('…');
            if (cut > 0) prefix = status.Substring(0, cut);

            bool newStep = (prefix != _lastPrefix);
            if (newStep || pct - _lastPct >= 10 || pct >= 100)
            {
                _lastPrefix = prefix;
                _lastPct = pct;
                Out(string.Format(CultureInfo.InvariantCulture, "  [{0,3}%] {1}", pct, status));
            }
        }

        private static void Out(string line)
        {
            try { Console.WriteLine(line); } catch { }
            _report.AppendLine(line);
        }

        private static void Flush()
        {
            if (string.IsNullOrEmpty(_reportPath)) return;
            try
            {
                File.WriteAllText(_reportPath, _report.ToString(), new UTF8Encoding(true));
            }
            catch { }
        }
    }
}
