using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.Win32;

// C# 5 兼容
namespace ClaudeCodeSetup
{
    internal sealed class InstallOptions
    {
        internal string InstallDir;
        internal string GitBash;
        internal bool UseBundled = true;
        internal string Arch = "";
        internal bool AddToPath = true;
        internal bool CreateDesktopShortcut = true;
        internal bool CreateStartMenuShortcut = true;
        /// <summary>是否改动系统状态（环境变量、卸载注册项）。沙箱测试时置 false。</summary>
        internal bool SystemChanges = true;
    }

    internal sealed class InstallResult
    {
        internal bool Ok;
        internal string Error = "";
        internal string InstalledVersion = "";   // 纯版本号，如 "2.1.268"
        internal string VersionOutput = "";      // claude --version 的原始输出
        internal readonly List<string> VerifyLines = new List<string>();
        internal readonly List<string> VerifyFailures = new List<string>();
        internal long InstalledBytes;
    }

    internal static class InstallerCore
    {
        internal const string PayloadEntry = "claude.exe";
        internal const string LauncherName = "launch.cmd";
        internal const string UninstallerName = "Uninstall.exe";

        // ─────────────────────────────────────────────────────────
        // 安装
        // ─────────────────────────────────────────────────────────
        internal static InstallResult Install(string selfPath, InstallOptions opt,
                                              ProgressSink progress, LogSink log)
        {
            InstallResult r = new InstallResult();
            string dir = opt.InstallDir;
            string exePath = Path.Combine(dir, "claude.exe");
            string tmpPath = Path.Combine(dir, "claude.exe.tmp");
            string tmpZip = null;      // ARM64 下载路径的临时 zip

            bool touchedSystem = false;
            try
            {
                // ── 步骤 1：准备目录 ────────────────────────────
                Report(progress, log, 2, "准备安装目录…");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Log.Write("安装目录: " + dir);

                // ── 步骤 2：取得 claude.exe ─────────────────────
                // x64 用内置载荷（离线、秒装）；ARM64 无内置载荷，按需从官方 release 下载。
                SafeDelete(tmpPath);

                if (opt.UseBundled)
                {
                    Report(progress, log, 5, "解压 Claude Code 二进制…");
                    PayloadRef p = Payload.Locate(selfPath);
                    if (!p.Found) throw new InvalidOperationException("未找到内置载荷，安装包可能已损坏。");
                    ExtractEntry(selfPath, p, PayloadEntry, tmpPath, progress, log);
                }
                else
                {
                    string url = Const.ReleaseRepo + "/releases/download/" +
                                 BuildInfo.ReleaseTag + "/" + BuildInfo.Arm64AssetName;
                    Log.Write("ARM64 架构：从官方 release 下载载荷");
                    Log.Write("URL: " + url);

                    tmpZip = Path.Combine(Path.GetTempPath(),
                        "claude-arm64-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip");

                    Report(progress, log, 5, "下载 ARM64 载荷…");
                    string dlErr;
                    if (!Net.DownloadFile(url, tmpZip, progress, out dlErr))
                    {
                        throw new InvalidOperationException(
                            "下载 ARM64 载荷失败：" + dlErr +
                            "\n可手动下载后重试：\n" + url);
                    }

                    Report(progress, log, 52, "校验下载载荷 SHA256…");
                    string gotZip = Util.Sha256File(tmpZip);
                    Log.Write("下载载荷 SHA256 = " + gotZip);

                    if (!string.IsNullOrEmpty(BuildInfo.Arm64Sha256) &&
                        !string.Equals(gotZip, BuildInfo.Arm64Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "下载载荷 SHA256 与官方 SHASUMS256.txt 不符，出于安全考虑已中止安装。\n" +
                            "期望 " + BuildInfo.Arm64Sha256 + "\n实际 " + gotZip);
                    }
                    Log.Write("下载载荷哈希与官方一致");

                    Report(progress, log, 55, "解压 Claude Code 二进制…");
                    using (ZipArchive zip = ZipFile.OpenRead(tmpZip))
                        ExtractEntry(zip, PayloadEntry, tmpPath, progress, log);
                }

                long extracted = new FileInfo(tmpPath).Length;
                Log.Write(string.Format(CultureInfo.InvariantCulture, "解压完成：{0:N0} 字节", extracted));

                // ── 步骤 3：校验解压产物 SHA256 ─────────────────
                Report(progress, log, 55, "校验二进制 SHA256…");
                string got = Util.Sha256File(tmpPath, Map(progress, 55, 64, "校验二进制 SHA256"), extracted);
                Log.Write("解压产物 SHA256 = " + got);

                if (!string.IsNullOrEmpty(BuildInfo.BinarySha256) &&
                    !string.Equals(got, BuildInfo.BinarySha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "解压产物 SHA256 校验失败。\n期望 " + BuildInfo.BinarySha256 +
                        "\n实际 " + got + "\n安装包可能已损坏，请重新下载。");
                }
                if (!Util.Sha256File(tmpPath).Equals(got, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("二进制在写入过程中发生变化，安装中止。");

                // ── 步骤 4：原子落盘 ────────────────────────────
                if (File.Exists(exePath))
                {
                    Log.Write("已存在旧 claude.exe，将被覆盖");
                    SafeDelete(exePath);
                }
                File.Move(tmpPath, exePath);
                r.InstalledBytes = extracted;
                Log.Write("已写入 " + exePath);

                // ── 步骤 5：冒烟测试 ────────────────────────────
                Report(progress, log, 65, "运行冒烟测试 (claude --version)…");
                string verOut;
                int exit = RunCapture(exePath, "--version", dir, 60000, out verOut);
                Log.Write(string.Format(CultureInfo.InvariantCulture,
                    "claude --version 退出码={0} 输出={1}", exit, OneLine(verOut)));

                if (exit != 0)
                    throw new InvalidOperationException(
                        "冒烟测试失败：claude --version 退出码 " + exit + "\n" + verOut);
                if (verOut.IndexOf(BuildInfo.ReleaseVersion, StringComparison.Ordinal) < 0)
                {
                    Log.Write("警告：版本输出与期望不完全一致，仍继续安装");
                }
                // claude --version 输出形如 "2.1.268 (Claude Code)"，这里只取版本号，
                // 否则会被当作 DisplayVersion 写进注册表。
                r.InstalledVersion = FirstToken(verOut);
                r.VersionOutput = OneLine(verOut);

                // ── 步骤 6：写启动器 ────────────────────────────
                Report(progress, log, 72, "写入启动器…");
                WriteLauncher(dir);
                touchedSystem = true;

                // ── 步骤 7：环境变量 ────────────────────────────
                if (opt.AddToPath)
                {
                    Report(progress, log, 78, "配置环境变量 (用户级 PATH)…");
                    AddToUserPath(dir);
                }
                if (opt.SystemChanges && !string.IsNullOrEmpty(opt.GitBash))
                {
                    SetUserEnv("CLAUDE_CODE_GIT_BASH_PATH", opt.GitBash, RegistryValueKind.String);
                    Log.Write("CLAUDE_CODE_GIT_BASH_PATH = " + opt.GitBash);
                }

                // ── 步骤 8：快捷方式 ────────────────────────────
                if (opt.CreateDesktopShortcut)
                {
                    Report(progress, log, 84, "创建桌面快捷方式…");
                    string lnk = Path.Combine(Util.DesktopDir(), Const.ProductName + ".lnk");
                    CreateShortcut(lnk, dir);
                    Log.Write("桌面快捷方式: " + lnk);
                }
                if (opt.CreateStartMenuShortcut)
                {
                    string sm = Util.StartMenuDir();
                    if (!Directory.Exists(sm)) Directory.CreateDirectory(sm);
                    string lnk = Path.Combine(sm, Const.ProductName + ".lnk");
                    CreateShortcut(lnk, dir);
                    Log.Write("开始菜单快捷方式: " + lnk);
                }

                // ── 步骤 9：卸载器与注册表项 ────────────────────
                Report(progress, log, 90, "注册卸载信息…");
                WriteUninstaller(dir);
                // 版本号以构建期固定的官方版本为准，而不是解析 --version 的文本
                if (opt.SystemChanges) WriteUninstallRegistry(dir, BuildInfo.ReleaseVersion, r.InstalledBytes);
                else Log.Write("（沙箱模式：跳过卸载注册表项）");

                // ── 步骤 10：清单与日志 ─────────────────────────
                WriteManifest(dir, opt, r);
                Log.Write("已写入 manifest.json");

                Util.BroadcastEnvironmentChange();
                Log.Write("已广播 WM_SETTINGCHANGE");

                // 把预检期日志合并进安装目录
                try
                {
                    string dst = Path.Combine(dir, "install.log");
                    File.WriteAllText(dst, Log.All() + Environment.NewLine, Encoding.UTF8);
                    Log.Init(dst);
                }
                catch { }

                r.Ok = true;
                Report(progress, log, 96, "安装完成，开始完整性验证…");
            }
            catch (Exception ex)
            {
                r.Ok = false;
                r.Error = Net.Root(ex);
                SafeDelete(tmpPath);
                Log.Write("安装失败: " + ex.ToString());
                if (touchedSystem)
                {
                    Log.Write("回滚已写入的系统改动…");
                    try { Rollback(opt); } catch (Exception rex) { Log.Write("回滚异常: " + rex.Message); }
                }
            }
            finally
            {
                if (tmpZip != null) { try { File.Delete(tmpZip); } catch { } }
            }
            return r;
        }

        // ─────────────────────────────────────────────────────────
        // 安装后完整性验证
        // ─────────────────────────────────────────────────────────
        internal static void Verify(InstallOptions opt, InstallResult r, ProgressSink progress, LogSink log)
        {
            string dir = opt.InstallDir;
            string exe = Path.Combine(dir, "claude.exe");

            // 1) 二进制存在且哈希正确
            if (File.Exists(exe))
            {
                long len = new FileInfo(exe).Length;
                string h = Util.Sha256File(exe);
                bool ok = string.IsNullOrEmpty(BuildInfo.BinarySha256) ||
                          string.Equals(h, BuildInfo.BinarySha256, StringComparison.OrdinalIgnoreCase);
                r.VerifyLines.Add(Fmt(ok, "二进制 claude.exe", string.Format(CultureInfo.InvariantCulture,
                    "{0:N0} 字节, SHA256 {1}…", len, h.Substring(0, 16))));
                if (!ok) r.VerifyFailures.Add("claude.exe 哈希不符");
            }
            else
            {
                r.VerifyLines.Add(Fmt(false, "二进制 claude.exe", "文件不存在"));
                r.VerifyFailures.Add("claude.exe 缺失");
            }

            // 2) 重读注册表确认 PATH
            if (opt.AddToPath)
            {
                string pv = ReadUserPathRaw();
                bool ok = Util.PathContains(pv, dir);
                int count = 0;
                if (!string.IsNullOrEmpty(pv))
                {
                    string[] parts = pv.Split(';');
                    for (int i = 0; i < parts.Length; i++)
                        if (Util.NormalizeDir(parts[i]) == Util.NormalizeDir(dir)) count++;
                }
                r.VerifyLines.Add(Fmt(ok && count == 1, "用户 PATH",
                    ok ? (count == 1 ? "已写入，无重复" : "已写入但出现 " + count + " 次") : "未写入"));
                if (!ok) r.VerifyFailures.Add("用户 PATH 未写入");
                else if (count != 1) r.VerifyFailures.Add("用户 PATH 存在重复项");
            }

            // 3) 用新 PATH 实际跑一次
            string oldPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", dir + ";" + oldPath);
                string outp;
                int code = RunCapture(exe, "--version", dir, 60000, out outp);
                bool ok = code == 0 && outp.IndexOf(BuildInfo.ReleaseVersion, StringComparison.Ordinal) >= 0;
                r.VerifyLines.Add(Fmt(ok, "启动测试 claude --version", OneLine(outp)));
                if (!ok) r.VerifyFailures.Add("claude --version 输出异常");
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", oldPath);
            }

            // 4) 快捷方式
            string[] links = new string[]
            {
                Path.Combine(Util.DesktopDir(), Const.ProductName + ".lnk"),
                Path.Combine(Util.StartMenuDir(), Const.ProductName + ".lnk")
            };
            string[] labels = new string[] { "桌面快捷方式", "开始菜单快捷方式" };
            for (int i = 0; i < links.Length; i++)
            {
                bool want = (i == 0) ? opt.CreateDesktopShortcut : opt.CreateStartMenuShortcut;
                if (!want) continue;
                bool exists = File.Exists(links[i]);
                r.VerifyLines.Add(Fmt(exists, labels[i], exists ? "已创建" : "缺失"));
                if (!exists) r.VerifyFailures.Add(labels[i] + "缺失");
            }

            // 5) 启动器
            string launcher = Path.Combine(dir, LauncherName);
            bool lk = File.Exists(launcher);
            r.VerifyLines.Add(Fmt(lk, "启动器 launch.cmd", lk ? "已写入" : "缺失"));
            if (!lk) r.VerifyFailures.Add("launch.cmd 缺失");

            // 6) 卸载器与注册表
            string un = Path.Combine(dir, UninstallerName);
            bool ue = File.Exists(un);
            r.VerifyLines.Add(Fmt(ue, "卸载器 Uninstall.exe", ue ? "已写入" : "缺失"));
            if (!ue) r.VerifyFailures.Add("Uninstall.exe 缺失");

            if (opt.SystemChanges)
            {
                bool regOk = false;
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Const.RegUninstall))
                        regOk = (k != null);
                }
                catch { }
                r.VerifyLines.Add(Fmt(regOk, "卸载注册表项", regOk ? @"HKCU\...\Uninstall\ClaudeCode" : "缺失"));
                if (!regOk) r.VerifyFailures.Add("卸载注册表项缺失");
            }
            else
            {
                r.VerifyLines.Add("[SKIP] 卸载注册表项            沙箱模式，未写入");
            }

            // 7) Git Bash 记录
            if (!opt.SystemChanges)
            {
                r.VerifyLines.Add("[SKIP] CLAUDE_CODE_GIT_BASH_PATH  沙箱模式，未写入");
            }
            else if (!string.IsNullOrEmpty(opt.GitBash))
            {
                string v = Environment.GetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH",
                                                             EnvironmentVariableTarget.User);
                bool ok = !string.IsNullOrEmpty(v);
                r.VerifyLines.Add(Fmt(ok, "CLAUDE_CODE_GIT_BASH_PATH", ok ? v : "未写入"));
                if (!ok) r.VerifyFailures.Add("CLAUDE_CODE_GIT_BASH_PATH 未写入");
            }
            else
            {
                string v = Environment.GetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH",
                                                             EnvironmentVariableTarget.User);
                if (!string.IsNullOrEmpty(v))
                    r.VerifyLines.Add(Fmt(true, "CLAUDE_CODE_GIT_BASH_PATH", "沿用既有设置"));
            }

            if (progress != null) progress(100, "验证完成");
        }

        // ─────────────────────────────────────────────────────────
        // 卸载
        // ─────────────────────────────────────────────────────────
        internal static bool Uninstall(string dir, LogSink log, bool keepUserConfig, out string error)
        {
            error = "";
            if (string.IsNullOrEmpty(dir)) dir = Util.InstallDir();
            try
            {
                Log.Write("开始卸载，安装目录: " + dir);

                // PATH
                RemoveFromUserPath(dir);
                Log.Write("已从用户 PATH 移除");

                // 环境变量：仅当指向本安装目录时才移除
                try
                {
                    string cur = Environment.GetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH",
                                                                    EnvironmentVariableTarget.User);
                    if (!string.IsNullOrEmpty(cur))
                    {
                        Environment.SetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH", null,
                                                           EnvironmentVariableTarget.User);
                        Log.Write("已移除 CLAUDE_CODE_GIT_BASH_PATH");
                    }
                }
                catch { }

                // 快捷方式
                DeleteIfExists(Path.Combine(Util.DesktopDir(), Const.ProductName + ".lnk"));
                DeleteIfExists(Path.Combine(Util.StartMenuDir(), Const.ProductName + ".lnk"));
                try
                {
                    string sm = Util.StartMenuDir();
                    if (Directory.Exists(sm) && Directory.GetFileSystemEntries(sm).Length == 0)
                        Directory.Delete(sm, false);
                }
                catch { }
                Log.Write("已移除快捷方式");

                // 注册表
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(Const.RegUninstall, false);
                    Log.Write("已移除卸载注册表项");
                }
                catch (Exception ex) { Log.Write("移除注册表项失败: " + ex.Message); }

                // 安装目录。卸载器自身正在运行会被占用，此时安排延迟删除。
                bool dirRemoved = false;
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    dirRemoved = !Directory.Exists(dir);
                    Log.Write("已删除安装目录");
                }
                catch (Exception ex)
                {
                    Log.Write("安装目录被占用，将安排延迟删除: " + ex.Message);
                }
                if (!dirRemoved)
                {
                    ScheduleDelete(dir);
                    Log.Write("已安排退出后删除残留目录");
                }

                // 用户配置
                if (!keepUserConfig)
                {
                    string claudeHome = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
                    try
                    {
                        if (Directory.Exists(claudeHome))
                        {
                            // 护栏：确认这确实是 Claude 的配置目录再删。
                            // 用户主目录异常/被重定向时，宁可留下也不误删无关内容。
                            if (LooksLikeClaudeHome(claudeHome))
                            {
                                Directory.Delete(claudeHome, true);
                                Log.Write("已删除用户配置 " + claudeHome);
                            }
                            else
                            {
                                Log.Write("拒绝删除 " + claudeHome + "：未发现 Claude 配置特征，已保留。");
                            }
                        }
                    }
                    catch (Exception ex) { Log.Write("删除用户配置失败: " + ex.Message); }
                }
                else
                {
                    Log.Write("按用户选择保留 ~/.claude 配置");
                }

                Util.BroadcastEnvironmentChange();
                return true;
            }
            catch (Exception ex)
            {
                error = Net.Root(ex);
                Log.Write("卸载失败: " + ex.ToString());
                return false;
            }
        }

        /// <summary>
        /// 判断目录是否确实是 Claude 的用户配置目录。
        /// 只要出现任一已知特征文件/子目录即认可。
        /// </summary>
        private static bool LooksLikeClaudeHome(string home)
        {
            string[] markers = new string[]
            {
                "settings.json",
                "settings.local.json",
                ".credentials.json",
                "projects",
                "todos",
                "statsig",
                "shell-snapshots"
            };
            for (int i = 0; i < markers.Length; i++)
            {
                try
                {
                    if (File.Exists(Path.Combine(home, markers[i]))) return true;
                    if (Directory.Exists(Path.Combine(home, markers[i]))) return true;
                }
                catch { }
            }
            return false;
        }

        private static void Rollback(InstallOptions opt)
        {
            string dir = opt.InstallDir;
            try { RemoveFromUserPath(dir); } catch { }
            try { DeleteIfExists(Path.Combine(Util.DesktopDir(), Const.ProductName + ".lnk")); } catch { }
            try { DeleteIfExists(Path.Combine(Util.StartMenuDir(), Const.ProductName + ".lnk")); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(Const.RegUninstall, false); } catch { }
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            try { Util.BroadcastEnvironmentChange(); } catch { }
            Log.Write("回滚完成");
        }

        // ─────────────────────────────────────────────────────────
        // 辅助
        // ─────────────────────────────────────────────────────────

        private static void Report(ProgressSink progress, LogSink log, int pct, string status)
        {
            Log.Write(status);
            if (log != null) log(status);
            if (progress != null) progress(pct, status);
        }

        /// <summary>
        /// 把子步骤自身的 0–100 进度线性映射到整体进度的 [lo, hi] 区间。
        /// 否则子步骤会把自己的百分比直接打到总进度条上，造成进度条倒退。
        /// </summary>
        private static ProgressSink Map(ProgressSink outer, int lo, int hi, string label)
        {
            if (outer == null) return null;
            return delegate(int pct, string ignored)
            {
                int p = lo + (int)((hi - lo) * (pct / 100.0));
                outer(p, string.Format(CultureInfo.InvariantCulture, "{0}… {1}%", label, pct));
            };
        }

        internal static void ExtractEntry(string selfPath, PayloadRef p, string entryName,
                                          string destPath, ProgressSink progress, LogSink log)
        {
            using (ZipArchive zip = Payload.Open(selfPath, p))
            {
                ExtractEntry(zip, entryName, destPath, progress, log);
            }
        }

        internal static void ExtractEntry(ZipArchive zip, string entryName,
                                          string destPath, ProgressSink progress, LogSink log)
        {
            {
                ZipArchiveEntry e = zip.GetEntry(entryName);
                if (e == null) throw new InvalidOperationException("载荷中找不到 " + entryName);

                long total = e.Length;
                string dir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                using (Stream input = e.Open())
                using (FileStream output = new FileStream(destPath, FileMode.Create, FileAccess.Write,
                                                          FileShare.None, 1024 * 1024))
                {
                    byte[] buf = new byte[1024 * 1024];
                    long done = 0;
                    int n;
                    int lastPct = -1;
                    while ((n = input.Read(buf, 0, buf.Length)) > 0)
                    {
                        output.Write(buf, 0, n);
                        done += n;
                        if (progress != null && total > 0)
                        {
                            // 解压阶段占总体进度 5% → 50%
                            int pct = 5 + (int)(done * 45 / total);
                            if (pct != lastPct)
                            {
                                lastPct = pct;
                                progress(pct, string.Format(CultureInfo.InvariantCulture,
                                    "解压中… {0:F0}% ({1:F0}/{2:F0} MB)",
                                    done * 100.0 / total, done / 1048576.0, total / 1048576.0));
                            }
                        }
                    }
                    output.Flush();
                }
            }
        }

        private static void WriteLauncher(string dir)
        {
            // %~dp0 自带尾部反斜杠；启动前把安装目录并入 PATH，避免环境变量尚未刷新导致找不到命令
            StringBuilder sb = new StringBuilder();
            sb.Append("@echo off\r\n");
            sb.Append("title ").Append(Const.ProductName).Append("\r\n");
            sb.Append("setlocal\r\n");
            sb.Append("set \"PATH=%~dp0;%PATH%\"\r\n");
            sb.Append("if \"%~1\"==\"\" (\r\n");
            sb.Append("  cd /d \"%USERPROFILE%\"\r\n");
            sb.Append("  \"%~dp0claude.exe\"\r\n");
            sb.Append(") else (\r\n");
            sb.Append("  \"%~dp0claude.exe\" %*\r\n");
            sb.Append(")\r\n");
            sb.Append("endlocal\r\n");
            File.WriteAllText(Path.Combine(dir, LauncherName), sb.ToString(), Encoding.Default);
        }

        private static void CreateShortcut(string lnkPath, string dir)
        {
            string launcher = Path.Combine(dir, LauncherName);
            string claudeExe = Path.Combine(dir, "claude.exe");
            string icon = claudeExe + ",0";
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string desc = Const.ProductName + " — 在终端中启动";

            string wt = Util.FindWindowsTerminal();
            if (!string.IsNullOrEmpty(wt))
            {
                // Windows Terminal：-d 指定起始目录，随后交给 cmd 执行启动器
                string args = "-d \"" + profile + "\" cmd.exe /k \"" + launcher + "\"";
                Shortcut.Create(lnkPath, wt, args, profile, desc, icon);
            }
            else
            {
                string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                          "cmd.exe");
                string args = "/k \"" + launcher + "\"";
                Shortcut.Create(lnkPath, cmd, args, profile, desc, icon);
            }
        }

        // ── 用户级 PATH 读写（保持 REG_EXPAND_SZ 类型）──────────
        internal static string ReadUserPathRaw()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey("Environment", false))
                {
                    if (k == null) return "";
                    object o = k.GetValue("Path", "",
                        RegistryValueOptions.DoNotExpandEnvironmentNames);
                    return o == null ? "" : o.ToString();
                }
            }
            catch { return ""; }
        }

        private static RegistryValueKind ReadUserPathKind()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey("Environment", false))
                {
                    if (k == null) return RegistryValueKind.ExpandString;
                    return k.GetValueKind("Path");
                }
            }
            catch { return RegistryValueKind.ExpandString; }
        }

        internal static void AddToUserPath(string dir)
        {
            string cur = ReadUserPathRaw();
            if (Util.PathContains(cur, dir))
            {
                Log.Write("用户 PATH 已包含安装目录，跳过");
                return;
            }
            string next = Util.PrependPath(cur, dir);
            RegistryValueKind kind = ReadUserPathKind();
            if (kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString)
                kind = RegistryValueKind.ExpandString;

            using (RegistryKey k = Registry.CurrentUser.OpenSubKey("Environment", true))
            {
                if (k == null) throw new InvalidOperationException("无法写入 HKCU\\Environment");
                k.SetValue("Path", next, kind);
            }
            Log.Write(string.Format(CultureInfo.InvariantCulture,
                "用户 PATH 已更新（{0} → {1} 项，类型 {2}）",
                CountEntries(cur), CountEntries(next), kind));
        }

        internal static void RemoveFromUserPath(string dir)
        {
            string cur = ReadUserPathRaw();
            if (!Util.PathContains(cur, dir)) return;

            string[] parts = cur.Split(';');
            List<string> keep = new List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                if (Util.NormalizeDir(parts[i]) == Util.NormalizeDir(dir)) continue;
                keep.Add(parts[i]);
            }
            string next = string.Join(";", keep.ToArray());
            RegistryValueKind kind = ReadUserPathKind();
            if (kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString)
                kind = RegistryValueKind.ExpandString;

            using (RegistryKey k = Registry.CurrentUser.OpenSubKey("Environment", true))
            {
                if (k == null) throw new InvalidOperationException("无法写入 HKCU\\Environment");
                k.SetValue("Path", next, kind);
            }
        }

        private static int CountEntries(string p)
        {
            if (string.IsNullOrEmpty(p)) return 0;
            int n = 0;
            string[] parts = p.Split(';');
            for (int i = 0; i < parts.Length; i++) if (parts[i].Trim().Length > 0) n++;
            return n;
        }

        private static void SetUserEnv(string name, string value, RegistryValueKind kind)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey("Environment", true))
            {
                if (k == null) throw new InvalidOperationException("无法写入 HKCU\\Environment");
                k.SetValue(name, value, kind);
            }
        }

        // ── 卸载器与注册表 ──────────────────────────────────────

        private static void WriteUninstaller(string dir)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            string resName = null;
            string[] names = asm.GetManifestResourceNames();
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i].EndsWith(UninstallerName, StringComparison.OrdinalIgnoreCase))
                {
                    resName = names[i];
                    break;
                }
            }
            if (resName == null) throw new InvalidOperationException("未嵌入卸载器资源");

            string dest = Path.Combine(dir, UninstallerName);
            using (Stream s = asm.GetManifestResourceStream(resName))
            using (FileStream fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                s.CopyTo(fs);
            }
            Log.Write("卸载器已写入，大小 " + new FileInfo(dest).Length + " 字节");
        }

        private static void WriteUninstallRegistry(string dir, string version, long installedBytes)
        {
            string display = Const.ProductName + " " + version;
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Const.RegUninstall))
            {
                if (k == null) throw new InvalidOperationException("无法创建卸载注册表项");
                k.SetValue("DisplayName",     display,                                RegistryValueKind.String);
                k.SetValue("DisplayVersion",  version,                                RegistryValueKind.String);
                k.SetValue("Publisher",       Const.Publisher,                        RegistryValueKind.String);
                k.SetValue("InstallLocation", dir,                                    RegistryValueKind.String);
                k.SetValue("UninstallString", "\"" + Path.Combine(dir, UninstallerName) + "\"",
                                                                                      RegistryValueKind.String);
                k.SetValue("QuietUninstallString",
                    "\"" + Path.Combine(dir, UninstallerName) + "\" /quiet",          RegistryValueKind.String);
                k.SetValue("DisplayIcon",     Path.Combine(dir, "claude.exe") + ",0", RegistryValueKind.String);
                k.SetValue("NoModify",        1,                                      RegistryValueKind.DWord);
                k.SetValue("NoRepair",        1,                                      RegistryValueKind.DWord);
                // 控制面板「大小」按 KB 显示，取实际安装字节数换算
                k.SetValue("EstimatedSize",   (int)Math.Min(int.MaxValue, installedBytes / 1024),
                                                                                      RegistryValueKind.DWord);
            }
        }

        private static void WriteManifest(string dir, InstallOptions opt, InstallResult r)
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            StringBuilder sb = new StringBuilder();
            sb.Append("{\r\n");
            sb.Append("  \"product\": \"").Append(Const.ProductName).Append("\",\r\n");
            sb.Append("  \"version\": \"").Append(BuildInfo.ReleaseVersion).Append("\",\r\n");
            sb.Append("  \"releaseTag\": \"").Append(BuildInfo.ReleaseTag).Append("\",\r\n");
            sb.Append("  \"sourceUrl\": \"").Append(BuildInfo.ReleaseUrl).Append("\",\r\n");
            sb.Append("  \"asset\": \"").Append(BuildInfo.AssetName).Append("\",\r\n");
            sb.Append("  \"assetSha256\": \"").Append(BuildInfo.PayloadSha256).Append("\",\r\n");
            sb.Append("  \"binarySha256\": \"").Append(BuildInfo.BinarySha256).Append("\",\r\n");
            sb.Append("  \"architecture\": \"").Append(opt.Arch).Append("\",\r\n");
            sb.Append("  \"installerVersion\": \"").Append(Const.InstallerVer).Append("\",\r\n");
            sb.Append("  \"installedAt\": \"").Append(
                DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)).Append("\",\r\n");
            sb.Append("  \"installDir\": \"").Append(JsonEscape(dir)).Append("\",\r\n");
            sb.Append("  \"binaryBytes\": ").Append(r.InstalledBytes.ToString(CultureInfo.InvariantCulture)).Append(",\r\n");
            sb.Append("  \"gitBashPath\": \"").Append(JsonEscape(opt.GitBash == null ? "" : opt.GitBash)).Append("\",\r\n");
            sb.Append("  \"userProfile\": \"").Append(JsonEscape(profile)).Append("\"\r\n");
            sb.Append("}\r\n");
            File.WriteAllText(Path.Combine(dir, "manifest.json"), sb.ToString(), new UTF8Encoding(false));
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // ── 进程执行 ────────────────────────────────────────────

        internal static int RunCapture(string exe, string args, string workDir, int timeoutMs, out string output)
        {
            output = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = workDir;

                StringBuilder sb = new StringBuilder();
                using (Process p = new Process())
                {
                    p.StartInfo = psi;
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                    { if (e.Data != null) lock (sb) { sb.AppendLine(e.Data); } };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                    { if (e.Data != null) lock (sb) { sb.AppendLine(e.Data); } };

                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        output = "执行超时（" + timeoutMs + " ms）";
                        return -1;
                    }
                    p.WaitForExit();
                    lock (sb) { output = sb.ToString().Trim(); }
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                output = Net.Root(ex);
                return -2;
            }
        }

        /// <summary>取首个空白分隔的 token，用于把 "2.1.268 (Claude Code)" 归一成 "2.1.268"。</summary>
        internal static string FirstToken(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string t = s.Trim();
            int i = t.IndexOfAny(new char[] { ' ', '\t', '\r', '\n' });
            if (i > 0) t = t.Substring(0, i);
            return t.Trim();
        }

        private static string OneLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(无输出)";
            string t = s.Replace("\r", " ").Replace("\n", " ").Trim();
            while (t.IndexOf("  ", StringComparison.Ordinal) >= 0) t = t.Replace("  ", " ");
            if (t.Length > 200) t = t.Substring(0, 200) + "…";
            return t;
        }

        private static string Fmt(bool ok, string label, string detail)
        {
            return (ok ? "[OK]   " : "[FAIL] ") + label.PadRight(26) + detail;
        }

        /// <summary>
        /// 卸载器进程自身占着安装目录，无法立即删除。
        /// 起一个脱离的 cmd，等本进程退出后再清理残留目录。
        /// </summary>
        internal static void ScheduleDelete(string dir)
        {
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(),
                    "cc-uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".cmd");
                StringBuilder sb = new StringBuilder();
                sb.Append("@echo off\r\n");
                sb.Append("rem 等待卸载器进程退出并释放文件句柄\r\n");
                sb.Append("ping -n 4 127.0.0.1 > nul 2>&1\r\n");
                sb.Append("rd /s /q \"").Append(dir.TrimEnd('\\')).Append("\" > nul 2>&1\r\n");
                sb.Append("del /f /q \"%~f0\" > nul 2>&1\r\n");
                File.WriteAllText(tmp, sb.ToString(), Encoding.Default);

                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c \"" + tmp + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log.Write("安排延迟删除失败: " + ex.Message);
            }
        }

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void DeleteIfExists(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
