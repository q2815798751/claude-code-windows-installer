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
