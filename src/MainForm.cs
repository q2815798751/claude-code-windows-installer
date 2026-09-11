using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// C# 5 兼容
namespace ClaudeCodeSetup
{
    internal sealed class MainForm : Form
    {
        // ── 控件 ────────────────────────────────────────────────
        private Panel     _header;
        private Label     _title;
        private Label     _subtitle;

        private Panel     _pageCheck;
        private Label     _checkCaption;
        private RichTextBox _rtbChecks;
        private Panel     _summary;
        private Label     _sumLines;

        private Panel     _pageInstall;
        private Label     _statusLabel;
        private ProgressBar _bar;
        private RichTextBox _rtbLog;

        private Button    _btnPrimary;
        private Button    _btnSecondary;

        // ── 状态 ────────────────────────────────────────────────
        private readonly string _selfPath;
        private PreflightResult _pre;
        private InstallResult   _result;
        private InstallOptions  _opt;
        private volatile bool   _busy;
        private bool            _installDone;
        private int             _checkIndex;
        private string          _logPath;

        private static readonly Color C_Pass   = Color.FromArgb(16, 124, 16);
        private static readonly Color C_Warn   = Color.FromArgb(186, 108, 0);
        private static readonly Color C_Fail   = Color.FromArgb(196, 43, 28);
        private static readonly Color C_Info   = Color.FromArgb(0, 103, 184);
        private static readonly Color C_Dim    = Color.FromArgb(96, 96, 96);
        private static readonly Color C_Header = Color.FromArgb(38, 38, 42);

        internal MainForm(string selfPath)
        {
            _selfPath = selfPath;
            BuildUi();
        }

        // ─────────────────────────────────────────────────────────
        // 界面构建
        // ─────────────────────────────────────────────────────────
        private void BuildUi()
        {
            Text            = Const.ProductName + " 一键安装程序";
            ClientSize      = new Size(780, 660);
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            Font            = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            BackColor       = Color.White;

            // 顶部标题条
            _header = new Panel();
            _header.Dock = DockStyle.Top;
            _header.Height = 76;
            _header.BackColor = C_Header;
            Controls.Add(_header);

            _title = new Label();
            _title.Text = Const.ProductName + " 一键安装程序";
            _title.ForeColor = Color.White;
            _title.Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold, GraphicsUnit.Point);
            _title.AutoSize = true;
            _title.Location = new Point(22, 14);
            _header.Controls.Add(_title);

            _subtitle = new Label();
            _subtitle.Text = "内置官方 " + BuildInfo.ReleaseTag + " 二进制 · 离线安装 · 无需管理员权限";
            _subtitle.ForeColor = Color.FromArgb(190, 190, 195);
            _subtitle.AutoSize = true;
            _subtitle.Location = new Point(24, 47);
            _header.Controls.Add(_subtitle);

            // 底部按钮条
            Panel buttonBar = new Panel();
            buttonBar.Dock = DockStyle.Bottom;
            buttonBar.Height = 58;
            buttonBar.BackColor = Color.FromArgb(246, 246, 248);
            Controls.Add(buttonBar);

            _btnPrimary = new Button();
            _btnPrimary.Text = "安  装";
            _btnPrimary.Size = new Size(130, 34);
            _btnPrimary.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            _btnPrimary.Location = new Point(buttonBar.Width - 290, 12);
            _btnPrimary.FlatStyle = FlatStyle.System;
            _btnPrimary.Click += OnPrimary;
            buttonBar.Controls.Add(_btnPrimary);
            _btnPrimary.Enabled = false;

            _btnSecondary = new Button();
            _btnSecondary.Text = "取  消";
            _btnSecondary.Size = new Size(130, 34);
            _btnSecondary.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            _btnSecondary.Location = new Point(buttonBar.Width - 148, 12);
            _btnSecondary.FlatStyle = FlatStyle.System;
            _btnSecondary.Click += OnSecondary;
            buttonBar.Controls.Add(_btnSecondary);

            buttonBar.Resize += delegate(object s, EventArgs e)
            {
                _btnPrimary.Location   = new Point(buttonBar.Width - 290, 12);
                _btnSecondary.Location = new Point(buttonBar.Width - 148, 12);
            };

            // ── 页面 A：预检 ────────────────────────────────────
            _pageCheck = new Panel();
            _pageCheck.Dock = DockStyle.Fill;
            _pageCheck.BackColor = Color.White;
            _pageCheck.Padding = new Padding(20, 12, 20, 6);
            Controls.Add(_pageCheck);
            _pageCheck.BringToFront();

            _checkCaption = new Label();
            _checkCaption.Text = "正在检查安装环境…";
            _checkCaption.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold, GraphicsUnit.Point);
            _checkCaption.Dock = DockStyle.Top;
            _checkCaption.Height = 26;
            _pageCheck.Controls.Add(_checkCaption);

            _summary = new Panel();
            _summary.Dock = DockStyle.Bottom;
            _summary.Height = 156;
            _summary.BackColor = Color.FromArgb(247, 249, 252);
            _summary.Padding = new Padding(14, 10, 14, 10);
            _pageCheck.Controls.Add(_summary);

            _sumLines = new Label();
            _sumLines.Dock = DockStyle.Fill;
            _sumLines.Font = new Font("Consolas", 9f, FontStyle.Regular, GraphicsUnit.Point);
            _sumLines.ForeColor = Color.FromArgb(40, 40, 40);
            _sumLines.Text = "";
            _summary.Controls.Add(_sumLines);

            _rtbChecks = new RichTextBox();
            _rtbChecks.Dock = DockStyle.Fill;
            _rtbChecks.ReadOnly = true;
            _rtbChecks.BorderStyle = BorderStyle.FixedSingle;
            _rtbChecks.BackColor = Color.White;
            _rtbChecks.Font = new Font("Microsoft YaHei UI", 9.25f, FontStyle.Regular, GraphicsUnit.Point);
            _rtbChecks.WordWrap = true;
            _rtbChecks.ScrollBars = RichTextBoxScrollBars.Vertical;
            _rtbChecks.DetectUrls = false;
            _rtbChecks.ShortcutsEnabled = true;
            _pageCheck.Controls.Add(_rtbChecks);
            _rtbChecks.BringToFront();

            // ── 页面 B：安装 ────────────────────────────────────
            _pageInstall = new Panel();
            _pageInstall.Dock = DockStyle.Fill;
            _pageInstall.BackColor = Color.White;
            _pageInstall.Padding = new Padding(20, 12, 20, 6);
            _pageInstall.Visible = false;
            Controls.Add(_pageInstall);

            _statusLabel = new Label();
            _statusLabel.Text = "准备中…";
            _statusLabel.Dock = DockStyle.Top;
            _statusLabel.Height = 24;
            _statusLabel.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point);
            _pageInstall.Controls.Add(_statusLabel);

            _bar = new ProgressBar();
            _bar.Dock = DockStyle.Top;
            _bar.Height = 20;
            _bar.Minimum = 0;
            _bar.Maximum = 100;
            _pageInstall.Controls.Add(_bar);

            Panel spacer = new Panel();
            spacer.Dock = DockStyle.Top;
            spacer.Height = 10;
            _pageInstall.Controls.Add(spacer);

            _rtbLog = new RichTextBox();
            _rtbLog.Dock = DockStyle.Fill;
            _rtbLog.ReadOnly = true;
            _rtbLog.BorderStyle = BorderStyle.FixedSingle;
            _rtbLog.BackColor = Color.FromArgb(252, 252, 252);
            _rtbLog.Font = new Font("Consolas", 9f, FontStyle.Regular, GraphicsUnit.Point);
            _rtbLog.WordWrap = true;
            _rtbLog.ScrollBars = RichTextBoxScrollBars.Vertical;
            _rtbLog.DetectUrls = false;
            _pageInstall.Controls.Add(_rtbLog);
            _rtbLog.BringToFront();

            _checkCaption.BringToFront();
            _summary.BringToFront();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            StartPreflight();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy && !_installDone)
            {
                DialogResult r = MessageBox.Show(this,
                    "安装正在进行，确定要退出吗？\n中途退出可能导致安装不完整。",
                    "确认退出", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) { e.Cancel = true; return; }
            }
            base.OnFormClosing(e);
        }

        // ─────────────────────────────────────────────────────────
        // 预检
        // ─────────────────────────────────────────────────────────
        private void StartPreflight()
        {
            _busy = true;
            _checkIndex = 0;
            _btnPrimary.Enabled = false;
            _btnSecondary.Text = "取  消";

            Thread t = new Thread(delegate()
            {
                try
                {
                    PreflightResult r = Preflight.Run(_selfPath, delegate(CheckItem item)
                    {
                        SafeInvoke(delegate { AppendCheck(item); });
                    });
                    SafeInvoke(delegate { OnPreflightDone(r); });
                }
                catch (Exception ex)
                {
                    SafeInvoke(delegate
                    {
                        _busy = false;
                        _checkCaption.Text = "预检失败";
                        AppendLine("✖ 预检过程出错：" + Net.Root(ex), C_Fail, true);
                        _btnPrimary.Enabled = false;
                        _btnSecondary.Text = "关  闭";
                    });
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void AppendCheck(CheckItem it)
        {
            string mark;
            Color col;
            bool bold = false;

            switch (it.State)
            {
                case CheckState.Pass:  mark = "✓"; col = C_Pass; break;
                case CheckState.Warn:  mark = "!"; col = C_Warn; bold = true; break;
                case CheckState.Fail:  mark = "✖"; col = C_Fail; bold = true; break;
                case CheckState.Info:  mark = "i"; col = C_Info; break;
                case CheckState.Running: mark = "…"; col = C_Dim; break;
                default: mark = "·"; col = C_Dim; break;
            }

            AppendLine(mark + "  " + it.Title, col, bold);

            if (!string.IsNullOrEmpty(it.Detail))
            {
                string[] dl = it.Detail.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < dl.Length; i++)
                    AppendLine("      " + dl[i], Color.FromArgb(64, 64, 64), false);
            }
            if (!string.IsNullOrEmpty(it.Advice))
            {
                string[] al = it.Advice.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < al.Length; i++)
                    AppendLine("      " + al[i], col, false);
            }
            AppendLine("", C_Dim, false);

            _checkIndex++;
            _checkCaption.Text = string.Format(CultureInfo.InvariantCulture,
                "正在检查安装环境…  ({0}/{1})", _checkIndex, 10);
        }

        private void OnPreflightDone(PreflightResult r)
        {
            _pre = r;
            _busy = false;

            int pass = 0, warn = 0, fail = 0, info = 0;
            for (int i = 0; i < r.Items.Count; i++)
            {
                switch (r.Items[i].State)
                {
                    case CheckState.Pass: pass++; break;
                    case CheckState.Warn: warn++; break;
                    case CheckState.Fail: fail++; break;
                    case CheckState.Info: info++; break;
                }
            }

            _checkCaption.Text = string.Format(CultureInfo.InvariantCulture,
                "环境检查完成 — 通过 {0} · 提示 {1} · 警告 {2} · 不通过 {3}", pass, info, warn, fail);
            _checkCaption.ForeColor = fail > 0 ? C_Fail : (warn > 0 ? C_Warn : C_Pass);

            BuildSummary(r);

            if (r.HasBlockingFailure)
            {
                _btnPrimary.Enabled = false;
                _btnPrimary.Text = "无法安装";
                _btnSecondary.Text = "关  闭";
                _btnSecondary.Focus();
            }
            else
            {
                _btnPrimary.Enabled = true;
                _btnPrimary.Text = r.AlreadyInstalled ? "覆盖安装 / 修复" : "安  装";
                _btnSecondary.Text = "取  消";
                _btnPrimary.Focus();
            }
        }

        private void BuildSummary(PreflightResult r)
        {
            StringBuilder sb = new StringBuilder();
            string dir = Util.InstallDir();

            sb.AppendLine("安装摘要");
            sb.AppendLine("  版本        " + Const.ProductName + " " + BuildInfo.ReleaseVersion +
                          "   (" + BuildInfo.ReleaseTag + ")");
            sb.AppendLine("  架构        " + (string.IsNullOrEmpty(r.Arch) ? "未知" : r.Arch) +
                          (r.UseBundled ? "  —  使用内置载荷，安装时无需联网" : "  —  需从 GitHub 下载"));
            sb.AppendLine("  安装位置    " + dir);
            sb.AppendLine("  占用空间    约 " + (r.InstallBytes / 1048576) + " MB");
            if (r.PayloadBytes > 0)
                sb.AppendLine("  载荷校验    SHA256 与官方 " + BuildInfo.ReleaseTag + " 一致");
            sb.AppendLine("  环境变更    新增安装目录 · 用户级 PATH · 桌面快捷方式 · 开始菜单 · 卸载项");

            _sumLines.Text = sb.ToString();
            _summary.Height = 18 * (sb.ToString().Split('\n').Length) + 22;
        }

        // ─────────────────────────────────────────────────────────
        // 安装 / 验证
        // ─────────────────────────────────────────────────────────
        private void OnPrimary(object sender, EventArgs e)
        {
            if (_installDone)
            {
                try { Process.Start("explorer.exe", Util.InstallDir()); }
                catch { }
                return;
            }
            if (_pre == null || _pre.HasBlockingFailure) return;

            DialogResult confirm = MessageBox.Show(this,
                "即将开始安装 " + Const.ProductName + " " + BuildInfo.ReleaseVersion + "。\n\n"
                + "安装位置：" + Util.InstallDir() + "\n"
                + "环境变更：写入用户级 PATH、创建桌面与开始菜单快捷方式、注册卸载项\n"
                + "全程无需管理员权限，不修改系统级设置。\n\n"
                + (_pre.HasWarning ? "注意：环境中存在警告项（详见检查结果），安装可以继续。\n\n" : "")
                + "确定现在安装吗？",
                "确认安装", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);

            if (confirm != DialogResult.OK) return;

            SwitchToInstallPage();
            _busy = true;
            _btnPrimary.Enabled = false;
            _btnSecondary.Enabled = false;

            _opt = new InstallOptions();
            _opt.InstallDir = Util.InstallDir();
            _opt.GitBash = _pre.GitBash;
            _opt.UseBundled = _pre.UseBundled;
            _opt.Arch = _pre.Arch;

            Thread t = new Thread(delegate()
            {
                try
                {
                    InstallResult res = InstallerCore.Install(_selfPath, _opt,
                        delegate(int pct, string st) { SafeInvoke(delegate { SetProgress(pct, st); }); },
                        delegate(string line) { SafeInvoke(delegate { AppendLog(line, Color.FromArgb(45, 45, 45)); }); });

                    _result = res;

                    SafeInvoke(delegate
                    {
                        if (!res.Ok)
                        {
                            AppendLog("", C_Dim);
                            AppendLog("✖ 安装失败：" + res.Error, C_Fail, true);
                            AppendLog("系统改动已回滚。", C_Warn, false);
                            FinishUi(false);
                            return;
                        }
                        AppendLog("", C_Dim);
                    });

                    if (!res.Ok) return;

                    InstallerCore.Verify(_opt, res,
                        delegate(int pct, string st) { SafeInvoke(delegate { SetProgress(pct, st); }); },
                        delegate(string line) { SafeInvoke(delegate { AppendLog(line, Color.FromArgb(45, 45, 45)); }); });

                    SafeInvoke(delegate { ShowVerifyReport(res); FinishUi(true); });
                }
                catch (Exception ex)
                {
                    SafeInvoke(delegate
                    {
                        AppendLog("✖ 安装过程异常：" + Net.Root(ex), C_Fail, true);
                        FinishUi(false);
                    });
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void SwitchToInstallPage()
        {
            _pageCheck.Visible = false;
            _pageInstall.Visible = true;
            _pageInstall.BringToFront();
            _statusLabel.Text = "准备中…";
            _bar.Value = 0;
        }

        /// <summary>
        /// 仅驱动进度条与状态文字。进度回调每秒可能触发几十次，绝不能在这里写日志，
        /// 否则日志区会被百分比刷屏。日志只由 log 回调按「步骤」粒度写入。
        /// </summary>
        private void SetProgress(int pct, string status)
        {
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;
            _bar.Value = pct;
            _statusLabel.Text = status;
        }

        private void ShowVerifyReport(InstallResult res)
        {
            AppendLog("", C_Dim);
            AppendLine2(_rtbLog, "完整性验证", C_Info, true);
            for (int i = 0; i < res.VerifyLines.Count; i++)
            {
                string line = res.VerifyLines[i];
                Color col;
                if (line.StartsWith("[OK]", StringComparison.Ordinal))        col = C_Pass;
                else if (line.StartsWith("[SKIP]", StringComparison.Ordinal)) col = Color.FromArgb(128, 128, 128);
                else                                                          col = C_Fail;
                AppendLog("  " + line, col, false);
            }
            AppendLog("", C_Dim);

            if (res.VerifyFailures.Count == 0)
            {
                AppendLog("✓  全部检查通过，安装成功。", C_Pass, true);
                AppendLog("    双击桌面「" + Const.ProductName + "」即可启动。", Color.FromArgb(60, 60, 60), false);
                if (string.IsNullOrEmpty(_opt.GitBash))
                {
                    AppendLog("", C_Dim);
                    AppendLog("!  未检测到 Git for Windows —— Claude Code 需要它才能执行命令。", C_Warn, true);
                    AppendLog("    补齐方式：命令行运行  winget install Git.Git", C_Warn, false);
                    AppendLog("    或访问  " + Const.GitDownloadUrl, C_Warn, false);
                }
            }
            else
            {
                AppendLog("!  安装完成，但有 " + res.VerifyFailures.Count + " 项检查未通过：", C_Warn, true);
                for (int i = 0; i < res.VerifyFailures.Count; i++)
                    AppendLog("      • " + res.VerifyFailures[i], C_Warn, false);
            }
        }

        private void FinishUi(bool ok)
        {
            _busy = false;
            _installDone = true;
            _bar.Value = 100;
            _statusLabel.Text = ok ? "安装完成" : "安装失败";
            _statusLabel.ForeColor = ok ? C_Pass : C_Fail;
            _btnPrimary.Text = ok ? "打开安装目录" : "关闭";
            _btnPrimary.Enabled = true;
            _btnSecondary.Text = ok ? "完  成" : "关  闭";
            _btnSecondary.Enabled = true;

            try
            {
                string logPath = Path.Combine(Util.InstallDir(), "install.log");
                if (File.Exists(logPath)) _logPath = logPath;
            }
            catch { }
        }

        private void OnSecondary(object sender, EventArgs e)
        {
            if (_installDone && _btnSecondary.Text.StartsWith("完", StringComparison.Ordinal))
            {
                Close();
                return;
            }
            Close();
        }

        // ─────────────────────────────────────────────────────────
        // 富文本辅助
        // ─────────────────────────────────────────────────────────
        private void AppendLine(string text, Color color, bool bold)
        {
            AppendLine2(_rtbChecks, text, color, bold);
        }

        private void AppendLog(string text, Color color)
        {
            AppendLog(text, color, false);
        }

        private void AppendLog(string text, Color color, bool bold)
        {
            AppendLine2(_rtbLog, text, color, bold);
        }

        private static void AppendLine2(RichTextBox rtb, string text, Color color, bool bold)
        {
            if (rtb == null) return;
            rtb.SelectionStart = rtb.TextLength;
            rtb.SelectionLength = 0;
            rtb.SelectionColor = color;
            rtb.SelectionFont = new Font(rtb.Font, bold ? FontStyle.Bold : FontStyle.Regular);
            rtb.AppendText(text + Environment.NewLine);
            rtb.SelectionColor = rtb.ForeColor;
            rtb.ScrollToCaret();
        }

        private void SafeInvoke(MethodInvoker mi)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                if (InvokeRequired) BeginInvoke(mi);
                else mi();
            }
            catch { }
        }
    }
}
