// ============================================================================
//  UnitTests.cs - zero-dependency assert-style unit tests for ClaudeCodeSetup.
//
//  Compiled and run by build\unit.bat using the in-box csc.exe (.NET Framework
//  4.x). No third-party test framework is used or allowed.
//
//  Language level: C# 5 only. Do NOT use $"" interpolation, nameof, ?. , ?[],
//  expression-bodied members or auto-property initializers.
//
//  The program exits with code 0 when every assertion passes, 1 otherwise, so
//  build\unit.bat can propagate a non-zero exit code to callers/CI.
//
//  Kept ASCII-only on purpose: test output must stay readable on any console
//  codepage and in CI logs.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ClaudeCodeSetup
{
    internal static class UnitTests
    {
        private static int _pass;
        private static int _fail;
        private static int _methods;
        private static readonly List<string> _failed = new List<string>();
        private static string _current = "(none)";

        // ---------------------------------------------------------------- harness

        private static void Begin(string name)
        {
            _current = name;
        }

        private static void Ok()
        {
            _pass++;
        }

        private static void Fail(string msg)
        {
            _fail++;
            _failed.Add(_current + ": " + msg);
            Console.WriteLine("  [FAIL] " + _current + " - " + msg);
        }

        private static void Check(bool cond, string what)
        {
            if (cond) Ok();
            else Fail(what);
        }

        private static string Show(string s)
        {
            if (s == null) return "<null>";
            return s.Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static void Eq(string expected, string actual, string what)
        {
            if (string.Equals(expected, actual, StringComparison.Ordinal)) Ok();
            else Fail(what + " (expected=[" + Show(expected) + "] actual=[" + Show(actual) + "])");
        }

        private static void EqInt(long expected, long actual, string what)
        {
            if (expected == actual) Ok();
            else Fail(what + " (expected=" + expected + " actual=" + actual + ")");
        }

        private static void Run(string name, Action body)
        {
            _methods++;
            Begin(name);
            int before = _fail;
            try
            {
                body();
            }
            catch (Exception ex)
            {
                Fail("threw " + ex.GetType().Name + ": " + ex.Message);
            }
            if (_fail == before) Console.WriteLine("  [ ok ] " + name);
        }

        private static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "cc-unit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        private static void RmTree(string d)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); }
            catch { }
        }

        // ---------------------------------------------------------------- tests

        // ---- Util.NormalizeDir --------------------------------------------

        private static void Test_NormalizeDir_Null()
        {
            Eq("", Util.NormalizeDir(null), "null normalizes to empty string");
        }

        private static void Test_NormalizeDir_TrailingSeparators()
        {
            Eq("c:\\git", Util.NormalizeDir("C:\\Git\\"), "trailing backslash stripped + lowercased");
            Eq("c:\\git", Util.NormalizeDir("C:\\Git///"), "all trailing separators stripped");
            Eq("c:\\git", Util.NormalizeDir("  C:\\Git  "), "surrounding whitespace trimmed");
        }

        private static void Test_NormalizeDir_Quotes()
        {
            Eq("c:\\git", Util.NormalizeDir("\"C:\\Git\""), "surrounding quotes stripped");
        }

        private static void Test_NormalizeDir_Lowercases()
        {
            Eq("c:\\program files\\git", Util.NormalizeDir("C:\\Program Files\\Git"),
                "path is lowercased for case-insensitive compare");
        }

        // ---- Util.PathContains --------------------------------------------

        private static void Test_PathContains_Found()
        {
            Check(Util.PathContains("C:\\Windows;C:\\Git\\bin", "C:\\Git\\bin"),
                "exact entry is found");
            Check(Util.PathContains("C:\\Windows;c:\\git\\BIN", "C:\\Git\\bin"),
                "match is case-insensitive");
            Check(Util.PathContains("C:\\Windows;C:\\Git\\bin\\", "C:\\Git\\bin"),
                "trailing slash on entry is ignored");
            Check(Util.PathContains("C:\\Git", "C:\\Git\\"),
                "trailing slash on needle is ignored");
        }

        private static void Test_PathContains_Missing()
        {
            Check(!Util.PathContains("C:\\Windows;D:\\Tools", "C:\\Git"),
                "absent directory is not reported as present");
            Check(!Util.PathContains("C:\\GitHub", "C:\\Git"),
                "prefix-only match must not count");
        }

        private static void Test_PathContains_Empty()
        {
            Check(!Util.PathContains("", "C:\\Git"), "empty PATH value returns false");
            Check(!Util.PathContains(null, "C:\\Git"), "null PATH value returns false");
        }

        // ---- Util.PrependPath ---------------------------------------------

        private static void Test_PrependPath_Prepends()
        {
            Eq("C:\\x;C:\\a;C:\\b", Util.PrependPath("C:\\a;C:\\b", "C:\\x"),
                "new dir is prepended in front of existing entries");
        }

        private static void Test_PrependPath_AlreadyPresent()
        {
            Eq("C:\\a;C:\\x;C:\\b", Util.PrependPath("C:\\a;C:\\x;C:\\b", "C:\\x"),
                "already-present dir leaves the value unchanged");
            Eq("C:\\a;c:\\x", Util.PrependPath("C:\\a;c:\\x", "C:\\X"),
                "presence check is case-insensitive (value returned as-is)");
        }

        private static void Test_PrependPath_Empty()
        {
            Eq("C:\\x", Util.PrependPath("", "C:\\x"), "empty value becomes just the dir");
            Eq("C:\\x", Util.PrependPath(null, "C:\\x"), "null value becomes just the dir");
        }

        private static void Test_PrependPath_TrimsLeadingSeparators()
        {
            Eq("C:\\x;C:\\a", Util.PrependPath(";;C:\\a", "C:\\x"),
                "leading semicolons are trimmed before prepending");
        }

        // ---- InstallerCore.FirstToken -------------------------------------

        private static void Test_FirstToken_Basic()
        {
            Eq("2.1.268", InstallerCore.FirstToken("2.1.268 (Claude Code)"),
                "first whitespace-delimited token is extracted");
            Eq("solo", InstallerCore.FirstToken("  solo  "), "single token is trimmed");
            Eq("a", InstallerCore.FirstToken("a\tb"), "tab also delimits tokens");
        }

        private static void Test_FirstToken_Empty()
        {
            Eq("", InstallerCore.FirstToken(""), "empty input yields empty");
            Eq("", InstallerCore.FirstToken(null), "null input yields empty");
        }

        // ---- Util.NativeArch (process-env injection) ----------------------

        private static void Test_NativeArch_PrefersW6432()
        {
            string old6432 = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432");
            string oldArch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
            try
            {
                Environment.SetEnvironmentVariable("PROCESSOR_ARCHITEW6432", "arm64");
                Environment.SetEnvironmentVariable("PROCESSOR_ARCHITECTURE", "AMD64");
                Eq("ARM64", Util.NativeArch(), "PROCESSOR_ARCHITEW6432 wins and is uppercased");
            }
            finally
            {
                Environment.SetEnvironmentVariable("PROCESSOR_ARCHITEW6432", old6432);
                Environment.SetEnvironmentVariable("PROCESSOR_ARCHITECTURE", oldArch);
            }
        }

        private static void Test_NativeArch_FallbackAndUpper()
        {
            string old6432 = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432");
            string oldArch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
            try
            {
                Environment.SetEnvironmentVariable("PROCESSOR_ARCHITEW6432", null);
                Environment.SetEnvironmentVariable("PROCESSOR_ARCHITECTURE", "x86");
                Eq("X86", Util.NativeArch(), "falls back to PROCESSOR_ARCHITECTURE, uppercased");

                Environment.SetEnvironmentVariable("PROCESSOR_ARCHITECTURE", null);
                Eq("", Util.NativeArch(), "empty string when no arch variable is set");
            }
            finally
            {
                Environment.SetEnvironmentVariable("PROCESSOR_ARCHITEW6432", old6432);
                Environment.SetEnvironmentVariable("PROCESSOR_ARCHITECTURE", oldArch);
            }
        }

        // ---- NetProbe -----------------------------------------------------

        private static void Test_NetProbe_Score()
        {
            NetProbe p = new NetProbe();
            EqInt(0, p.Score(), "no tcp/tls -> score 0");
            p.TcpOk = true;
            EqInt(1, p.Score(), "tcp only -> score 1");
            p.TlsOk = true;
            EqInt(2, p.Score(), "tls ok -> score 2");
        }

        private static void Test_NetProbe_Summary()
        {
            NetProbe p = new NetProbe();
            p.Host = "example.com";
            p.TlsOk = true;
            p.CertValid = true;
            p.TcpMs = 5;
            p.TlsMs = 7;
            string s = p.Summary();
            Check(s.StartsWith("example.com", StringComparison.Ordinal), "summary starts with host");
            Check(s.IndexOf("TLS 7ms", StringComparison.Ordinal) >= 0, "summary reports TLS ms");
        }

        // ---- Payload.Locate -----------------------------------------------

        private static void Test_Payload_Locate_Valid()
        {
            string d = TempDir();
            try
            {
                string f = Path.Combine(d, "blob.bin");
                byte[] payload = new byte[1000];
                for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);
                using (FileStream fs = new FileStream(f, FileMode.Create, FileAccess.Write))
                {
                    fs.Write(payload, 0, payload.Length);
                    byte[] len = BitConverter.GetBytes((long)payload.Length);
                    fs.Write(len, 0, len.Length);
                    byte[] magic = Encoding.ASCII.GetBytes(Const.PayloadMagic);
                    fs.Write(magic, 0, magic.Length);
                }

                PayloadRef r = Payload.Locate(f);
                Check(r.Found, "valid trailer is located");
                EqInt(1000, r.Length, "payload length is read from trailer");
                EqInt(0, r.Offset, "payload offset is computed from file length");
            }
            finally { RmTree(d); }
        }

        private static void Test_Payload_Locate_NoMagic()
        {
            string d = TempDir();
            try
            {
                string f = Path.Combine(d, "blob.bin");
                using (FileStream fs = new FileStream(f, FileMode.Create, FileAccess.Write))
                {
                    byte[] payload = new byte[100];
                    fs.Write(payload, 0, payload.Length);
                    byte[] len = BitConverter.GetBytes((long)payload.Length);
                    fs.Write(len, 0, len.Length);
                    byte[] junk = new byte[16];
                    for (int i = 0; i < junk.Length; i++) junk[i] = (byte)'X';
                    fs.Write(junk, 0, junk.Length);
                }
                Check(!Payload.Locate(f).Found, "wrong magic -> not found");
            }
            finally { RmTree(d); }
        }

        private static void Test_Payload_Locate_TooShort()
        {
            string d = TempDir();
            try
            {
                string f = Path.Combine(d, "blob.bin");
                File.WriteAllBytes(f, new byte[10]);
                Check(!Payload.Locate(f).Found, "file shorter than trailer -> not found");
            }
            finally { RmTree(d); }
        }

        // ---- SubStream ----------------------------------------------------

        private static void Test_SubStream_ReadSeek()
        {
            byte[] data = new byte[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19 };
            MemoryStream ms = new MemoryStream(data);
            SubStream sub = new SubStream(ms, 3, 4);
            EqInt(4, sub.Length, "sub stream length is the region length");

            byte[] buf = new byte[4];
            int n = sub.Read(buf, 0, 4);
            EqInt(4, n, "read returns region length");
            Check(buf[0] == 13 && buf[3] == 16, "reads the correct region bytes");
            EqInt(4, sub.Position, "position advanced by bytes read");

            sub.Seek(0, SeekOrigin.Begin);
            n = sub.Read(buf, 0, 2);
            EqInt(2, n, "read after seek returns requested count");
            Check(buf[0] == 13 && buf[1] == 14, "seek-to-begin re-reads from region start");

            n = sub.Read(buf, 0, 4);
            EqInt(2, n, "read is clamped at region end");
        }

        // ---- Util.Sha256File ----------------------------------------------

        private static void Test_Sha256File_Known()
        {
            string d = TempDir();
            try
            {
                string f = Path.Combine(d, "abc.txt");
                File.WriteAllText(f, "abc", new UTF8Encoding(false));
                Eq("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                   Util.Sha256File(f), "sha256 of 'abc' matches the known digest");
            }
            finally { RmTree(d); }
        }

        // ---- Const --------------------------------------------------------

        private static void Test_Const_RequiredFreeBytes()
        {
            EqInt(700L * 1024 * 1024, Const.RequiredFreeBytes,
                "required free space constant is 700 MB");
        }

        // ---- AW-1: InstallerCore.ShouldRemoveGitBashEnv -------------------

        private static void Test_ShouldRemoveGitBashEnv_PointsElsewhere()
        {
            // The bug: a Git Bash path the user configured *before* installing must
            // survive uninstall. It is neither recorded in the manifest nor inside
            // the install dir, so it must be kept.
            Check(!InstallerCore.ShouldRemoveGitBashEnv(
                    "E:\\Git\\bin\\bash.exe", false, "", "C:\\Users\\me\\AppData\\Local\\ClaudeCode"),
                "user-owned value outside the install dir is kept");
            Check(!InstallerCore.ShouldRemoveGitBashEnv(
                    "D:\\Tools\\PortableGit\\usr\\bin\\bash.exe", false, "", "C:\\CC"),
                "another unrelated user path is kept");
        }

        private static void Test_ShouldRemoveGitBashEnv_PointsIntoInstallDir()
        {
            Check(InstallerCore.ShouldRemoveGitBashEnv(
                    "C:\\CC\\git\\bin\\bash.exe", false, "", "C:\\CC"),
                "value under the install dir is removed");
            Check(InstallerCore.ShouldRemoveGitBashEnv(
                    "C:\\CC", false, "", "C:\\CC\\"),
                "value equal to the install dir (trailing slash) is removed");
            Check(!InstallerCore.ShouldRemoveGitBashEnv(
                    "C:\\CCX\\bin\\bash.exe", false, "", "C:\\CC"),
                "a sibling dir sharing a name prefix must not match");
        }

        private static void Test_ShouldRemoveGitBashEnv_Absent()
        {
            Check(!InstallerCore.ShouldRemoveGitBashEnv(null, true, "C:\\CC\\bin\\bash.exe", "C:\\CC"),
                "null value -> no operation");
            Check(!InstallerCore.ShouldRemoveGitBashEnv("", true, "C:\\CC\\bin\\bash.exe", "C:\\CC"),
                "empty value -> no operation");
        }

        private static void Test_ShouldRemoveGitBashEnv_ManifestOwnership()
        {
            // Installer wrote it and the user never changed it -> remove.
            Check(InstallerCore.ShouldRemoveGitBashEnv(
                    "C:\\Git\\bin\\bash.exe", true, "C:\\Git\\bin\\bash.exe", "C:\\CC"),
                "value still equal to the manifest record is removed");
            // Installer wrote it, but the user changed it afterwards -> keep.
            Check(!InstallerCore.ShouldRemoveGitBashEnv(
                    "D:\\Other\\bash.exe", true, "C:\\Git\\bin\\bash.exe", "C:\\CC"),
                "value changed since install is kept");
            // Old manifest without the field -> no ownership record -> keep.
            Check(!InstallerCore.ShouldRemoveGitBashEnv(
                    "C:\\Git\\bin\\bash.exe", false, "", "C:\\CC"),
                "old manifest without the field does not authorise removal");
        }

        private static void Test_PathIsUnder()
        {
            Check(InstallerCore.PathIsUnder("C:\\CC\\a\\b.exe", "C:\\CC"), "nested file is under dir");
            Check(InstallerCore.PathIsUnder("C:\\CC", "C:\\CC\\"), "dir equals itself");
            Check(InstallerCore.PathIsUnder("c:\\cc\\x", "C:\\CC"), "comparison is case-insensitive");
            Check(!InstallerCore.PathIsUnder("C:\\CCX\\y", "C:\\CC"), "prefix-only sibling is not under");
            Check(!InstallerCore.PathIsUnder("", "C:\\CC"), "empty path is not under anything");
            Check(!InstallerCore.PathIsUnder("C:\\CC\\a", ""), "empty dir matches nothing");
        }

        private static void Test_ReadGitBashManifest()
        {
            string d = TempDir();
            try
            {
                string rec;

                // New manifest: installer explicitly recorded that it wrote the var.
                File.WriteAllText(Path.Combine(d, "manifest.json"),
                    "{\r\n  \"product\": \"Claude Code\",\r\n  \"gitBashEnvWritten\": true,\r\n" +
                    "  \"gitBashPath\": \"C:\\\\Git\\\\bin\\\\bash.exe\"\r\n}\r\n",
                    new UTF8Encoding(false));
                bool wrote = InstallerCore.ReadGitBashManifest(d, out rec);
                Check(wrote, "written flag is read from the manifest");
                Eq("C:\\Git\\bin\\bash.exe", rec, "backslash-escaped path is unescaped");

                // Legacy manifest: no gitBashEnvWritten field -> not our responsibility.
                File.WriteAllText(Path.Combine(d, "manifest.json"),
                    "{\r\n  \"product\": \"Claude Code\",\r\n" +
                    "  \"gitBashPath\": \"C:\\\\Git\\\\bin\\\\bash.exe\"\r\n}\r\n",
                    new UTF8Encoding(false));
                wrote = InstallerCore.ReadGitBashManifest(d, out rec);
                Check(!wrote, "legacy manifest without the field reads as not-written");

                // Explicit false.
                File.WriteAllText(Path.Combine(d, "manifest.json"),
                    "{\r\n  \"gitBashEnvWritten\": false,\r\n  \"gitBashPath\": \"\"\r\n}\r\n",
                    new UTF8Encoding(false));
                wrote = InstallerCore.ReadGitBashManifest(d, out rec);
                Check(!wrote, "gitBashEnvWritten:false reads as not-written");

                // Missing file must not throw.
                wrote = InstallerCore.ReadGitBashManifest(Path.Combine(d, "nope.json"), out rec);
                Check(!wrote, "missing manifest reads as not-written");
            }
            finally { RmTree(d); }
        }

        // ---- AW-2: InstallerCore.VerifyLaunchViaPath ----------------------

        private static void Test_VerifyLaunchViaPath_UsesBareNameOnPath()
        {
            InstallResult r = new InstallResult();

            string seenCmd = null;
            string seenArgs = null;
            string seenWorkDir = null;
            string seenPath = null;
            int seenTimeout = -1;

            PathRunner stub = delegate(string command, string args, string workDir,
                                       int timeoutMs, string pathOverride, out string output)
            {
                seenCmd = command;
                seenArgs = args;
                seenWorkDir = workDir;
                seenTimeout = timeoutMs;
                seenPath = pathOverride;
                output = BuildInfo.ReleaseVersion + " (Claude Code)";
                return 0;
            };

            string beforePath = Environment.GetEnvironmentVariable("PATH");
            InstallerCore.VerifyLaunchViaPath("C:\\CC", r, stub);

            // The whole point of AW-2: the command must be the bare name, so it is
            // resolved through PATH. Passing "C:\CC\claude.exe" would bypass PATH
            // entirely and make the verification meaningless.
            Eq("claude", seenCmd, "launches the bare command name 'claude'");
            Check(seenCmd.IndexOf("\\", StringComparison.Ordinal) < 0 &&
                  seenCmd.IndexOf(":", StringComparison.Ordinal) < 0,
                  "command name is not an absolute path");
            Eq("--version", seenArgs, "passes --version");
            Check(seenTimeout > 0, "a positive timeout is used");

            // The new PATH (install dir first) must be handed to the child.
            Check(seenPath != null && seenPath.StartsWith("C:\\CC", StringComparison.Ordinal),
                  "the new PATH begins with the install dir");
            Check(Util.PathContains(seenPath, "C:\\CC"), "the new PATH contains the install dir");

            // The work dir must NOT be the install dir, otherwise the command could be
            // found via the current directory and bypass PATH again.
            Check(seenWorkDir == null || seenWorkDir.IndexOf("C:\\CC", StringComparison.Ordinal) < 0,
                  "working dir is neutral (not the install dir)");

            // Crucial: the current process environment must be left untouched.
            Eq(beforePath, Environment.GetEnvironmentVariable("PATH"),
               "current process PATH is not modified");

            EqInt(0, r.VerifyFailures.Count, "expected version output -> no verification failures");
            EqInt(1, r.VerifyLines.Count, "one verification line is recorded");
        }

        private static void Test_VerifyLaunchViaPath_ReportsFailure()
        {
            // Non-zero exit code (e.g. PATH did not resolve) -> must be recorded as a failure.
            InstallResult r1 = new InstallResult();
            PathRunner fail = delegate(string command, string args, string workDir,
                                       int timeoutMs, string pathOverride, out string output)
            {
                output = "'claude' is not recognized as an internal or external command";
                return 1;
            };
            InstallerCore.VerifyLaunchViaPath("C:\\CC", r1, fail);
            EqInt(1, r1.VerifyFailures.Count, "non-zero exit code is recorded as a failure");

            // Zero exit but unexpected output -> also a failure.
            // NOTE: the bogus output must NOT contain BuildInfo.ReleaseVersion for
            // ANY value, otherwise this assertion is a false red in a fresh clone
            // where unit.bat generates a stub with ReleaseVersion = "0.0.0"
            // (AW-16). A digit-free sentinel keeps the test version-agnostic.
            InstallResult r2 = new InstallResult();
            PathRunner wrongVer = delegate(string command, string args, string workDir,
                                           int timeoutMs, string pathOverride, out string output)
            {
                output = "unexpected-output-not-a-version";
                return 0;
            };
            InstallerCore.VerifyLaunchViaPath("C:\\CC", r2, wrongVer);
            EqInt(1, r2.VerifyFailures.Count, "unexpected version output is recorded as a failure");
        }

        // ---- AW-16: InstallerCore.RunViaPath really resolves through PATH ----
        //
        // This is the discriminating test AW-2's review asked for. The stub-based
        // VerifyLaunchViaPath tests only assert what is handed to the injected
        // runner; a stub never touches PATH, so those assertions cannot prove that
        // PATH is actually resolved. This test drives the *production* RunViaPath
        // against a fake claude.cmd that exists ONLY as a .cmd on a temp PATH:
        //   - PATH containing that dir  -> the command resolves and succeeds;
        //   - PATH without it           -> the command is not found and fails.
        // If the launch verification is ever changed back to a fake (e.g. resolved
        // by an absolute path that bypasses PATH), the bare "claude" would no
        // longer be found via PATH and this test goes red.
        private static void Test_RunViaPath_ResolvesThroughPath()
        {
            string root = TempDir();
            string binDir = Path.Combine(root, "bin");
            string emptyDir = Path.Combine(root, "empty");
            string workDir = Path.Combine(root, "work");
            Directory.CreateDirectory(binDir);
            Directory.CreateDirectory(emptyDir);
            Directory.CreateDirectory(workDir);
            try
            {
                // A fake "claude" that exists only as claude.cmd, so it can be found
                // exclusively through PATH (cmd.exe resolves .cmd via PATHEXT).
                string marker = "aw16-fake-claude-9.9.9";
                File.WriteAllText(Path.Combine(binDir, "claude.cmd"),
                    "@echo off\r\necho " + marker + "\r\n", new UTF8Encoding(false));

                // The work dir must be neutral (no claude.cmd), so resolution can
                // only happen through PATH and not via the current directory.
                string outp;
                int okCode = InstallerCore.RunViaPath("claude", "--version", workDir,
                                                      60000, binDir, out outp);
                EqInt(0, okCode, "RunViaPath resolves bare 'claude' via PATH (exit 0)");
                Check(outp != null && outp.IndexOf(marker, StringComparison.Ordinal) >= 0,
                      "RunViaPath returns the fake command's output");

                int failCode = InstallerCore.RunViaPath("claude", "--version", workDir,
                                                        60000, emptyDir, out outp);
                Check(failCode != 0, "RunViaPath fails when the command is not on PATH");
            }
            finally { RmTree(root); }
        }

        // ---- AW-3: Preflight.ExistingAdvice wording matches implementation ----
        //
        // CheckExisting() probes the real filesystem/PATH, so it cannot be driven
        // deterministically from a unit test. The user-visible advice text was
        // therefore extracted into the pure helper Preflight.ExistingAdvice so the
        // exact wording can be asserted here (AW-0 review flagged Checks.cs as
        // having zero coverage).
        //
        // The installer prepends the install dir to the user PATH
        // (Installer.AddToUserPath -> Util.PrependPath), so the advice must say the
        // dir is placed at the FRONT. The Chinese literals are written as \u escapes
        // to keep this file ASCII-only (per the file header).
        private static void Test_CheckExisting_AdviceSaysPrepend()
        {
            string dir = "C:\\AW3FakeInstall";
            string advice = Preflight.ExistingAdvice(dir);

            // Written as \u escapes so this file stays ASCII-only (see header).
            string prepend = "\u524d\u7f6e"; // = "prepended" (front of PATH)
            string append  = "\u672b\u5c3e"; // = "at the end" (tail of PATH)

            Check(advice != null && advice.IndexOf(prepend, StringComparison.Ordinal) >= 0,
                  "advice says the install dir is PREPENDED to PATH");
            Check(advice != null && advice.IndexOf(append, StringComparison.Ordinal) < 0,
                  "advice does NOT say the install dir goes at the END of PATH");
            Check(advice != null && advice.IndexOf(dir, StringComparison.Ordinal) >= 0,
                  "advice mentions the install dir");
        }

        // ---- AW-4: Preflight.ArchVerdict (32-bit Windows must be rejected) ----
        //
        // CheckArch() reads Util.NativeArch() from the process environment, so it
        // cannot be driven deterministically per branch from a unit test. The verdict
        // logic was therefore extracted into the pure, injectable helper
        // Preflight.ArchVerdict(arch, out useBundled) (same approach as AW-3's
        // ExistingAdvice). The bug: "X86" shared the AMD64 branch and returned Pass
        // with UseBundled=true, so a 32-bit Windows box installed a 64-bit claude.exe
        // and only failed at the smoke test.
        private static void Test_ArchVerdict_X86Rejected()
        {
            bool ub;

            CheckItem x86 = Preflight.ArchVerdict("X86", out ub);
            Check(x86.State != CheckState.Pass,
                  "32-bit Windows (X86) must not pass the arch check");
            Check(x86.State == CheckState.Fail,
                  "32-bit Windows (X86) is rejected (default decision)");
            Check(!string.IsNullOrEmpty(x86.Advice),
                  "the X86 rejection carries user-facing advice");

            // AMD64 is still supported and keeps using the bundled payload.
            CheckItem x64 = Preflight.ArchVerdict("AMD64", out ub);
            Check(x64.State == CheckState.Pass, "AMD64 still passes the arch check");
            Check(ub, "AMD64 uses the bundled payload");

            // ARM64 and the unknown-arch fallback keep their previous behaviour.
            CheckItem arm = Preflight.ArchVerdict("ARM64", out ub);
            Check(arm.State == CheckState.Info && !ub,
                  "ARM64 still reports Info and downloads (no bundled payload)");
            CheckItem unk = Preflight.ArchVerdict("", out ub);
            Check(unk.State == CheckState.Warn && ub,
                  "unknown arch still warns and assumes x64");
        }

        // ---------------------------------------------------------------- main

        internal static int Main(string[] args)
        {
            Console.WriteLine("===== ClaudeCodeSetup unit tests =====");

            Run("NormalizeDir_Null", Test_NormalizeDir_Null);
            Run("NormalizeDir_TrailingSeparators", Test_NormalizeDir_TrailingSeparators);
            Run("NormalizeDir_Quotes", Test_NormalizeDir_Quotes);
            Run("NormalizeDir_Lowercases", Test_NormalizeDir_Lowercases);

            Run("PathContains_Found", Test_PathContains_Found);
            Run("PathContains_Missing", Test_PathContains_Missing);
            Run("PathContains_Empty", Test_PathContains_Empty);

            Run("PrependPath_Prepends", Test_PrependPath_Prepends);
            Run("PrependPath_AlreadyPresent", Test_PrependPath_AlreadyPresent);
            Run("PrependPath_Empty", Test_PrependPath_Empty);
            Run("PrependPath_TrimsLeadingSeparators", Test_PrependPath_TrimsLeadingSeparators);

            Run("FirstToken_Basic", Test_FirstToken_Basic);
            Run("FirstToken_Empty", Test_FirstToken_Empty);

            Run("NativeArch_PrefersW6432", Test_NativeArch_PrefersW6432);
            Run("NativeArch_FallbackAndUpper", Test_NativeArch_FallbackAndUpper);

            Run("NetProbe_Score", Test_NetProbe_Score);
            Run("NetProbe_Summary", Test_NetProbe_Summary);

            Run("Payload_Locate_Valid", Test_Payload_Locate_Valid);
            Run("Payload_Locate_NoMagic", Test_Payload_Locate_NoMagic);
            Run("Payload_Locate_TooShort", Test_Payload_Locate_TooShort);

            Run("SubStream_ReadSeek", Test_SubStream_ReadSeek);

            Run("Sha256File_Known", Test_Sha256File_Known);

            Run("Const_RequiredFreeBytes", Test_Const_RequiredFreeBytes);

            Run("ShouldRemoveGitBashEnv_PointsElsewhere", Test_ShouldRemoveGitBashEnv_PointsElsewhere);
            Run("ShouldRemoveGitBashEnv_PointsIntoInstallDir", Test_ShouldRemoveGitBashEnv_PointsIntoInstallDir);
            Run("ShouldRemoveGitBashEnv_Absent", Test_ShouldRemoveGitBashEnv_Absent);
            Run("ShouldRemoveGitBashEnv_ManifestOwnership", Test_ShouldRemoveGitBashEnv_ManifestOwnership);
            Run("PathIsUnder", Test_PathIsUnder);
            Run("ReadGitBashManifest", Test_ReadGitBashManifest);

            Run("VerifyLaunchViaPath_UsesBareNameOnPath", Test_VerifyLaunchViaPath_UsesBareNameOnPath);
            Run("VerifyLaunchViaPath_ReportsFailure", Test_VerifyLaunchViaPath_ReportsFailure);
            Run("RunViaPath_ResolvesThroughPath", Test_RunViaPath_ResolvesThroughPath);

            Run("CheckExisting_AdviceSaysPrepend", Test_CheckExisting_AdviceSaysPrepend);

            Run("ArchVerdict_X86Rejected", Test_ArchVerdict_X86Rejected);

            Console.WriteLine("--------------------------------------");
            Console.WriteLine("test methods : " + _methods);
            Console.WriteLine("assertions   : " + (_pass + _fail) + "  (passed " + _pass + ", failed " + _fail + ")");
            if (_fail == 0)
            {
                Console.WriteLine("RESULT: PASS");
                return 0;
            }
            Console.WriteLine("RESULT: FAIL");
            for (int i = 0; i < _failed.Count; i++) Console.WriteLine("   - " + _failed[i]);
            return 1;
        }
    }
}
