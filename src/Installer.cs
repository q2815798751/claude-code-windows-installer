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

    /// <summary>
    /// 「按 PATH 执行一个命令」的可注入执行器（AW-2）。
    /// 契约：command 必须是**裸命令名**（交给 PATH 解析），而不是绝对路径；
    /// pathOverride 非空时只覆盖**子进程**的 PATH（临时进程环境），不污染当前进程。
    /// 可注入是为了让单测能断言「实际传给系统的是裸命令名而非绝对路径」。
    /// </summary>
    internal delegate int PathRunner(string command, string args, string workDir,
                                     int timeoutMs, string pathOverride, out string output);

    /// <summary>
    /// AW-6：文件系统操作的窄接口。抽出它是为了让「落盘失败」「覆盖安装回滚」这类
    /// **只在异常路径上发生**的分支能在单测里确定性地驱动——注入一个会让 MoveFile
    /// 抛异常的假实现即可，不必真的制造磁盘故障。生产代码用 RealFileOps。
    /// </summary>
    internal interface IFileOps
    {
        bool FileExists(string path);
        bool DirExists(string path);
        void MoveFile(string src, string dst);
        void DeleteFile(string path);
        void DeleteDir(string path);
    }

    /// <summary>IFileOps 的真实实现（直接转发到 System.IO）。</summary>
    internal sealed class RealFileOps : IFileOps
    {
        public bool FileExists(string path) { return File.Exists(path); }
        public bool DirExists(string path) { return Directory.Exists(path); }
        public void MoveFile(string src, string dst) { File.Move(src, dst); }
        public void DeleteFile(string path) { File.Delete(path); }
        public void DeleteDir(string path) { Directory.Delete(path, true); }
    }

    internal static class InstallerCore
    {
        internal const string PayloadEntry = "claude.exe";
        internal const string LauncherName = "launch.cmd";
        internal const string UninstallerName = "Uninstall.exe";

        // ─────────────────────────────────────────────────────────
        // AW-10：ARM64 下载临时文件的命名与清理
        // ─────────────────────────────────────────────────────────
        // 旧实现把 ARM64 载荷下载到 %TEMP%\claude-arm64-<随机8位>.zip，且只在 finally 里删除。
        // 进程被杀（用户取消、任务管理器结束、断电）时 finally 不会执行，约 96 MB 临时文件残留；
        // 因为文件名带随机 GUID，下一轮安装**无法定位**它，残留永远无人认领。
        //
        // 契约（本项修复）：
        //   - 命名**可预测**：固定的 Arm64TempZipName（不含随机数），因此上一轮的任何残留
        //     都能被下一次安装用同一个名字找到并删除。
        //   - **下载之前**先清理同名旧文件（PrepareArm64TempZip），把上一轮被杀留下的残留
        //     回收，而不是只依赖 finally（它只在正常退出时运行）。两条路径互补。
        //
        // 为什么不用 FileOptions.DeleteOnClose？DeleteOnClose 只在**文件句柄**关闭时删除，
        // 而下载（Net.DownloadFile 按路径写）与解压（ZipFile.OpenRead 二次打开）分属不同句柄；
        // 要让“崩溃后回收”成立就得让该句柄一直活到解压结束，改动面远大于固定名 + 预清理，
        // 且 FileShare 语义会让二次打开失败。故采用固定名 + 下载前清理（改动小、可测）。
        internal const string Arm64TempZipName = "claude-arm64-payload.zip";

        /// <summary>
        /// AW-10：ARM64 下载临时 zip 的**可预测**路径（固定文件名，不含随机数）。
        /// </summary>
        internal static string Arm64TempZipPath(string tempDir)
        {
            return Path.Combine(tempDir, Arm64TempZipName);
        }

        /// <summary>
        /// AW-10：删除指定路径上的上一轮残留临时 zip。存在则删除并返回 true，
        /// 不存在或无权限时返回 false（best-effort，绝不抛出）。
        /// </summary>
        internal static bool CleanStaleTempZip(string path, IFileOps fs)
        {
            try
            {
                if (fs.FileExists(path)) { fs.DeleteFile(path); return true; }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// AW-10：准备 ARM64 下载的临时 zip 路径，并在**下载之前**清理上一轮残留。
        /// 返回可预测的固定路径，交给 Net.DownloadFile 写入。
        /// </summary>
        internal static string PrepareArm64TempZip(string tempDir, IFileOps fs)
        {
            string path = Arm64TempZipPath(tempDir);
            CleanStaleTempZip(path, fs);
            return path;
        }

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
            // AW-6(b)：本次是不是「覆盖/修复安装」——必须在任何安装动作**之前**、按安装前的
            // 文件系统状态判定，并一直传到回滚点。覆盖安装中途失败时不能整体删除安装目录
            // （那是用户原有的安装），只能回滚到备份状态。
            bool wasOverwrite = IsOverwriteInstall(dir, new RealFileOps());
            // 记录本次安装是否真的写入了用户级 CLAUDE_CODE_GIT_BASH_PATH。
            // 卸载时据此判断该变量是不是本安装器的“责任”，避免误删用户自有的设置（AW-1）。
            bool gitBashEnvWritten = false;
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

                    // AW-10：可预测的固定临时文件名 + **下载前**清理上一轮残留。
                    // 若上一次安装被杀（finally 未执行）留下约 96 MB 的残留 zip，这里会用
                    // 同一个名字定位并删除它，避免残留无限累积。
                    tmpZip = PrepareArm64TempZip(Path.GetTempPath(), new RealFileOps());
                    Log.Write("ARM64 临时载荷: " + tmpZip + "（下载前已清理上一轮同名残留）");

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
                // AW-6(a)：先把已存在的旧 claude.exe 改名备份为 claude.exe.old，再把新产物
                // Move 到位。旧实现先 SafeDelete(exePath) 再 File.Move，Move 一失败用户原有的
                // claude.exe 就已经没了——并不“原子”。备份保留到安装整体成功后才提交删除，
                // 因此中途失败时回滚可以还原它（AW-6b）。
                if (File.Exists(exePath))
                    Log.Write("已存在旧 claude.exe，将备份为 claude.exe.old 后覆盖");
                AtomicPlace(exePath, tmpPath, new RealFileOps());
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
                    gitBashEnvWritten = true;
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
                WriteManifest(dir, opt, r, gitBashEnvWritten);
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
                // AW-6：安装整体成功，提交事务——删除 claude.exe.old 备份。
                CommitBackup(exePath, new RealFileOps());
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
                    try { Rollback(opt, wasOverwrite); } catch (Exception rex) { Log.Write("回滚异常: " + rex.Message); }
                }
                else if (wasOverwrite)
                {
                    // AW-6：尚未写系统改动，但步骤 4 可能已产生 claude.exe.old 备份（例如
                    // 步骤 5 冒烟测试失败）。还原它，让覆盖安装失败后用户的 claude.exe 回到
                    // 安装前的版本，并避免留下 .old 残留。仅覆盖安装才需要（全新安装没有备份）。
                    try { RestoreOverwriteBackup(exePath, new RealFileOps()); }
                    catch (Exception rex) { Log.Write("还原备份异常: " + rex.Message); }
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

            // 3) 用新 PATH 实际跑一次（走 PATH 解析，而不是绝对路径 —— AW-2）
            VerifyLaunchViaPath(dir, r, RunViaPath);

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

        /// <summary>
        /// AW-2：用**新 PATH** 实际启动一次 `claude --version`，验证 PATH 生效链路。
        ///
        /// 契约（旧实现违反之处）：命令名必须传**裸名** "claude"（交给 PATH 解析），
        /// 而不是 `dir\claude.exe` 的绝对路径 —— 传绝对路径时这个验证根本不经过 PATH，
        /// 是假验证（README / PLAN 宣称的「用新 PATH 实际启动」从未被真正验证）。
        ///
        /// 新 PATH 只注入临时子进程环境（runner 的 pathOverride），不动当前进程/系统。
        /// 工作目录刻意用中性目录：若用安装目录，命令可能被「当前目录」直接命中而绕过
        /// PATH（cmd 默认会搜当前目录），那样又变成假验证。
        /// </summary>
        internal static void VerifyLaunchViaPath(string dir, InstallResult r, PathRunner runner)
        {
            string oldPath = Environment.GetEnvironmentVariable("PATH");
            if (oldPath == null) oldPath = "";
            string newPath = Util.PrependPath(oldPath, dir);

            // 中性工作目录：不含 claude.exe，确保命令只能靠 PATH 解析到。
            string workDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(workDir)) workDir = Path.GetTempPath();

            string outp;
            int code = runner("claude", "--version", workDir, 60000, newPath, out outp);
            bool ok = code == 0 && outp != null &&
                      outp.IndexOf(BuildInfo.ReleaseVersion, StringComparison.Ordinal) >= 0;
            r.VerifyLines.Add(Fmt(ok, "启动测试 claude --version", OneLine(outp)));
            if (!ok) r.VerifyFailures.Add("claude --version 输出异常");
        }

        /// <summary>
        /// 真正按 PATH 执行：借 `cmd.exe /c` 让子进程按 pathOverride 里的 PATH 解析裸命令名。
        ///
        /// 为什么不能直接 Process.Start("claude", …) 配 EnvironmentVariables["PATH"]？
        /// CreateProcess 解析裸命令名用的是**调用进程**的 PATH，对子进程环境块里的 PATH
        /// 覆盖无效 —— 本机实测直接传裸名会「系统找不到指定的文件」。cmd.exe 则按自身
        /// 环境里的 PATH 解析，因此既能真正走 PATH，又不污染当前进程。
        /// </summary>
        internal static int RunViaPath(string command, string args, string workDir,
                                       int timeoutMs, string pathOverride, out string output)
        {
            string cmdExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            return RunCapture(cmdExe, "/c " + command + " " + args, workDir, timeoutMs,
                              pathOverride, out output);
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

                // 环境变量：仅当该变量由本安装器写入（或明确指向本次安装目录）时才移除。
                // 用户装 Claude Code 之前自己配好的 Git Bash 路径不能被卸载顺手删掉（AW-1）。
                // manifest 在下面删除安装目录之前读取。
                try
                {
                    string cur = Environment.GetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH",
                                                                    EnvironmentVariableTarget.User);
                    string recorded;
                    bool wrote = ReadGitBashManifest(dir, out recorded);
                    if (string.IsNullOrEmpty(cur))
                    {
                        // 变量不存在：无操作，不报错
                    }
                    else if (ShouldRemoveGitBashEnv(cur, wrote, recorded, dir))
                    {
                        Environment.SetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH", null,
                                                           EnvironmentVariableTarget.User);
                        Log.Write("已移除本安装器写入的 CLAUDE_CODE_GIT_BASH_PATH");
                    }
                    else
                    {
                        Log.Write("保留用户自有的 CLAUDE_CODE_GIT_BASH_PATH: " + cur);
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

        // ─────────────────────────────────────────────────────────
        // AW-6：原子落盘 / 回滚（覆盖安装不误删既有安装）
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// AW-6(b)：判断本次安装是否为「覆盖/修复安装」——安装开始前目标目录是否已有
        /// 既有的安装（claude.exe 或 manifest.json）。必须在任何安装动作之前、按安装前的
        /// 文件系统状态判定。返回 true 表示目录里已有用户的既有安装，回滚时不能整体删除。
        /// </summary>
        internal static bool IsOverwriteInstall(string dir, IFileOps fs)
        {
            if (fs.FileExists(Path.Combine(dir, "claude.exe"))) return true;
            if (fs.FileExists(Path.Combine(dir, "manifest.json"))) return true;
            return false;
        }

        /// <summary>
        /// AW-6(a)：原子落盘。先把已存在的旧 claude.exe 改名备份为 &lt;exe&gt;.old，再把新产物
        /// Move 到位；Move 失败时把 .old 改回原名，用户原有的 claude.exe 保持原样。
        /// 备份**不在此处删除**：它保留到安装整体成功（CommitBackup）或由回滚
        /// （RollbackDir / RestoreOverwriteBackup）还原，作为覆盖安装的事务回滚点（AW-6b）。
        /// 落盘成功返回 true；失败时抛出异常（此时旧 exe 已还原）。
        /// </summary>
        internal static bool AtomicPlace(string exePath, string tmpPath, IFileOps fs)
        {
            string oldPath = exePath + ".old";
            bool hadOld = fs.FileExists(exePath);
            if (hadOld)
            {
                // 清掉上一轮可能残留的备份，避免 Move 因目标已存在而失败。
                try { if (fs.FileExists(oldPath)) fs.DeleteFile(oldPath); } catch { }
                fs.MoveFile(exePath, oldPath);
            }
            try
            {
                fs.MoveFile(tmpPath, exePath);
            }
            catch
            {
                // 落盘失败：把备份改回原名，恢复用户原有的 claude.exe，再向上抛。
                if (hadOld)
                {
                    try { fs.MoveFile(oldPath, exePath); } catch { }
                }
                throw;
            }
            return true;
        }

        /// <summary>
        /// AW-6(a)：安装整体成功后提交事务——删除 claude.exe.old 备份。best-effort，
        /// 删除失败不影响安装结果（残留的 .old 只是多余文件，不影响 claude.exe）。
        /// </summary>
        internal static void CommitBackup(string exePath, IFileOps fs)
        {
            try
            {
                string oldPath = exePath + ".old";
                if (fs.FileExists(oldPath)) fs.DeleteFile(oldPath);
            }
            catch { }
        }

        /// <summary>
        /// AW-6：把 claude.exe.old 备份还原回 claude.exe（撤销本次覆盖）。若没有备份
        /// （全新安装，或落盘本身就已回滚）则为无操作。best-effort，绝不抛出。
        /// </summary>
        internal static void RestoreOverwriteBackup(string exePath, IFileOps fs)
        {
            string oldPath = exePath + ".old";
            if (!fs.FileExists(oldPath)) return;
            try { if (fs.FileExists(exePath)) fs.DeleteFile(exePath); } catch { }
            try { fs.MoveFile(oldPath, exePath); } catch { }
        }

        /// <summary>
        /// AW-6(b)：把安装目录回滚到安装前的状态。
        /// 契约：
        ///   - 全新安装（wasOverwrite == false）：删除安装目录——它是本安装器本次创建的。
        ///   - 覆盖/修复安装（wasOverwrite == true）：**绝不整体删除**用户原有的安装目录
        ///     （旧实现无条件 Directory.Delete(dir, true)，会把用户原有安装一并删掉）。
        ///     若有 claude.exe.old 备份则还原回 claude.exe（撤销本次覆盖），否则原样保留。
        /// 返回 true 表示删除了目录（全新安装），false 表示保留了既有安装。
        /// </summary>
        internal static bool RollbackDir(string dir, string exePath, bool wasOverwrite, IFileOps fs)
        {
            if (wasOverwrite)
            {
                RestoreOverwriteBackup(exePath, fs);
                return false;
            }
            try { if (fs.DirExists(dir)) fs.DeleteDir(dir); } catch { }
            return true;
        }

        private static void Rollback(InstallOptions opt, bool wasOverwrite)
        {
            string dir = opt.InstallDir;
            string exePath = Path.Combine(dir, "claude.exe");
            try { RemoveFromUserPath(dir); } catch { }
            try { DeleteIfExists(Path.Combine(Util.DesktopDir(), Const.ProductName + ".lnk")); } catch { }
            try { DeleteIfExists(Path.Combine(Util.StartMenuDir(), Const.ProductName + ".lnk")); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(Const.RegUninstall, false); } catch { }
            bool dirDeleted = false;
            try { dirDeleted = RollbackDir(dir, exePath, wasOverwrite, new RealFileOps()); } catch { }
            if (dirDeleted) Log.Write("回滚完成：已删除本次创建的安装目录");
            else Log.Write("回滚完成：保留既有安装（覆盖安装，未删除目录；已还原备份）");
            try { Util.BroadcastEnvironmentChange(); } catch { }
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

        private static void WriteManifest(string dir, InstallOptions opt, InstallResult r,
                                          bool gitBashEnvWritten)
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
            // gitBashEnvWritten 表示“本安装器写入了用户级 CLAUDE_CODE_GIT_BASH_PATH”；
            // gitBashPath 是当时写入的值。卸载器据此判断该变量是否由本安装器负责（AW-1）。
            // 未写入时两者留空，老版本安装器产出的 manifest 没有 gitBashEnvWritten 字段。
            sb.Append("  \"gitBashEnvWritten\": ").Append(gitBashEnvWritten ? "true" : "false").Append(",\r\n");
            sb.Append("  \"gitBashPath\": \"").Append(
                JsonEscape(gitBashEnvWritten && opt.GitBash != null ? opt.GitBash : "")).Append("\",\r\n");
            sb.Append("  \"userProfile\": \"").Append(JsonEscape(profile)).Append("\"\r\n");
            sb.Append("}\r\n");
            File.WriteAllText(Path.Combine(dir, "manifest.json"), sb.ToString(), new UTF8Encoding(false));
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string JsonUnescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        // ── AW-1：卸载时该不该删用户级 CLAUDE_CODE_GIT_BASH_PATH ──────────

        /// <summary>
        /// 卸载时是否应移除用户级 CLAUDE_CODE_GIT_BASH_PATH。
        /// 契约（兑现 Uninstall 里的注释）：仅当该变量确由本安装器写入、且用户此后没改过它，
        /// 或者该值明确落在本次安装目录内（本安装器管理的 Git Bash）时才移除。
        ///   - currentValue 为空 → 无操作（false）。
        ///   - manifest 记录本安装器写过（manifestWroteIt）且当前值仍等于当时写入的值 → 移除。
        ///   - 当前值位于 installDir 之下 → 移除。
        ///   - 其余（用户在安装前自配、安装后改过、或老 manifest 无该记录）→ 保留。
        /// </summary>
        internal static bool ShouldRemoveGitBashEnv(string currentValue, bool manifestWroteIt,
                                                    string recordedValue, string installDir)
        {
            if (string.IsNullOrEmpty(currentValue)) return false;

            if (manifestWroteIt && !string.IsNullOrEmpty(recordedValue) &&
                string.Equals(currentValue.Trim(), recordedValue.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;

            return PathIsUnder(currentValue, installDir);
        }

        /// <summary>path 是否就是 dir 本身或位于 dir 之下（大小写不敏感）。</summary>
        internal static bool PathIsUnder(string path, string dir)
        {
            string p = Util.NormalizeDir(path);
            string d = Util.NormalizeDir(dir);
            if (p.Length == 0 || d.Length == 0) return false;
            if (p == d) return true;
            return p.StartsWith(d + "\\", StringComparison.Ordinal);
        }

        /// <summary>
        /// 从安装目录的 manifest.json 读取“本安装器是否写入过 CLAUDE_CODE_GIT_BASH_PATH”
        /// 及当时写入的值。返回值 true 表示 manifest 明确记录写过该变量；
        /// recordedValue 为当时写入的值（无记录时为空串）。老 manifest 无该字段时返回 false，
        /// 卸载器因此不会据它删除（向后兼容）。
        /// </summary>
        internal static bool ReadGitBashManifest(string dir, out string recordedValue)
        {
            recordedValue = "";
            try
            {
                string path = Path.Combine(dir, "manifest.json");
                if (!File.Exists(path)) return false;
                string text = File.ReadAllText(path, Encoding.UTF8);
                if (!JsonBool(text, "gitBashEnvWritten")) return false;
                recordedValue = JsonString(text, "gitBashPath");
                return true;
            }
            catch { return false; }
        }

        private static string JsonString(string json, string key)
        {
            string needle = "\"" + key + "\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return "";
            int c = json.IndexOf(':', i + needle.Length);
            if (c < 0) return "";
            int q1 = json.IndexOf('"', c + 1);
            if (q1 < 0) return "";
            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return "";
            return JsonUnescape(json.Substring(q1 + 1, q2 - q1 - 1));
        }

        private static bool JsonBool(string json, string key)
        {
            string needle = "\"" + key + "\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return false;
            int c = json.IndexOf(':', i + needle.Length);
            if (c < 0) return false;
            string rest = json.Substring(c + 1).TrimStart();
            return rest.StartsWith("true", StringComparison.OrdinalIgnoreCase);
        }

        // ── 进程执行 ────────────────────────────────────────────

        internal static int RunCapture(string exe, string args, string workDir, int timeoutMs, out string output)
        {
            return RunCapture(exe, args, workDir, timeoutMs, null, out output);
        }

        /// <summary>
        /// 与上面同，但 pathOverride 非空时只覆盖**子进程**的 PATH（临时进程环境），
        /// 当前进程的环境不受影响（AW-2）。
        /// </summary>
        internal static int RunCapture(string exe, string args, string workDir, int timeoutMs,
                                       string pathOverride, out string output)
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
                // 只改子进程的环境块，绝不碰当前进程的环境变量。
                if (pathOverride != null) psi.EnvironmentVariables["PATH"] = pathOverride;

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
