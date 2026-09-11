using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// C# 5 兼容
// 编译：csc /target:winexe Uninstaller.cs Common.cs Installer.cs BuildInfo.cs
namespace ClaudeCodeSetup
{
    internal sealed class UninstallerForm : Form
    {
        private Label        _headline;
        private Label        _detail;
        private CheckBox     _keepConfig;
        private RichTextBox  _log;
        private ProgressBar  _bar;
        private Button       _btnGo;
        private Button       _btnCancel;

        private readonly string _dir;
        private volatile bool   _busy;
        private bool            _done;

        internal UninstallerForm(string dir)
        {
            _dir = dir;
            BuildUi();
        }

        private void BuildUi()
        {
            Text            = "卸载 " + Const.ProductName;
            ClientSize      = new Size(640, 500);
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            Font            = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            BackColor       = Color.White;

            Panel head = new Panel();
            head.Dock = DockStyle.Top;
            head.Height = 68;
            head.BackColor = Color.FromArgb(38, 38, 42);
            Controls.Add(head);

            Label t = new Label();
            t.Text = "卸载 " + Const.ProductName;
            t.ForeColor = Color.White;
            t.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold, GraphicsUnit.Point);
            t.AutoSize = true;
            t.Location = new Point(20, 16);
            head.Controls.Add(t);

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 56;
            bottom.BackColor = Color.FromArgb(246, 246, 248);
            Controls.Add(bottom);

            _btnGo = new Button();
            _btnGo.Text = "卸  载";
            _btnGo.Size = new Size(120, 32);
            _btnGo.FlatStyle = FlatStyle.System;
            _btnGo.Location = new Point(380, 12);
            _btnGo.Click += OnGo;
            bottom.Controls.Add(_btnGo);

            _btnCancel = new Button();
            _btnCancel.Text = "取  消";
            _btnCancel.Size = new Size(120, 32);
            _btnCancel.FlatStyle = FlatStyle.System;
            _btnCancel.Location = new Point(508, 12);
            _btnCancel.Click += delegate(object s, EventArgs e) { Close(); };
            bottom.Controls.Add(_btnCancel);

            Panel body = new Panel();
            body.Dock = DockStyle.Fill;
            body.Padding = new Padding(20, 14, 20, 8);
            Controls.Add(body);
            body.BringToFront();

            _headline = new Label();
            _headline.Dock = DockStyle.Top;
            _headline.Height = 24;
            _headline.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold, GraphicsUnit.Point);
            _headline.Text = "即将卸载 Claude Code";
            body.Controls.Add(_headline);

            _detail = new Label();
            _detail.Dock = DockStyle.Top;
            _detail.Height = 96;
            _detail.ForeColor = Color.FromArgb(60, 60, 60);
            _detail.Text =
                "安装目录：" + _dir + "\r\n\r\n" +
                "将移除：程序文件、用户级 PATH 条目、桌面与开始菜单快捷方式、卸载注册表项。\r\n" +
                "不会触碰：系统级设置、其他软件的 PATH 条目。";
            body.Controls.Add(_detail);

            _keepConfig = new CheckBox();
            _keepConfig.Dock = DockStyle.Top;
            _keepConfig.Height = 26;
            _keepConfig.Text = "保留我的 Claude 配置与登录信息 (%USERPROFILE%\\.claude)";
            _keepConfig.Checked = true;
            body.Controls.Add(_keepConfig);

            Panel gap = new Panel();
            gap.Dock = DockStyle.Top;
            gap.Height = 8;
            body.Controls.Add(gap);

            _bar = new ProgressBar();
            _bar.Dock = DockStyle.Top;
            _bar.Height = 18;
            _bar.Maximum = 100;
            body.Controls.Add(_bar);

            Panel gap2 = new Panel();
            gap2.Dock = DockStyle.Top;
            gap2.Height = 8;
            body.Controls.Add(gap2);

            _log = new RichTextBox();
            _log.Dock = DockStyle.Fill;
            _log.ReadOnly = true;
            _log.BorderStyle = BorderStyle.FixedSingle;
            _log.BackColor = Color.FromArgb(252, 252, 252);
            _log.Font = new Font("Consolas", 9f, FontStyle.Regular, GraphicsUnit.Point);
            _log.WordWrap = true;
            body.Controls.Add(_log);
            _log.BringToFront();
            _keepConfig.BringToFront();
            _bar.BringToFront();
            gap.BringToFront();
            gap2.BringToFront();
            _detail.BringToFront();
            _headline.BringToFront();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy && !_done)
            {
                DialogResult r = MessageBox.Show(this, "卸载正在进行，确定要退出吗？",
                    "确认退出", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) { e.Cancel = true; return; }
            }
            base.OnFormClosing(e);
        }

        private void OnGo(object sender, EventArgs e)
        {
            if (_done) { Close(); return; }

            if (MessageBox.Show(this,
                "确定要卸载 Claude Code 吗？\n\n此操作会删除 " + _dir,
                "确认卸载", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            _busy = true;
            _btnGo.Enabled = false;
            _btnCancel.Enabled = false;
            _keepConfig.Enabled = false;

            bool keep = _keepConfig.Checked;

            Thread t = new Thread(delegate()
            {
                try
                {
                    Log.Init(Path.Combine(Path.GetTempPath(),
                        "ClaudeCodeUninstall-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log"));

                    SetBar(20);
                    string err;
                    bool ok = InstallerCore.Uninstall(_dir,
                        delegate(string line) { Safe(delegate { Append(line, Color.FromArgb(90, 90, 90)); }); },
                        keep, out err);
                    SetBar(100);

                    Safe(delegate
                    {
                        Append("", Color.Gray);
                        if (ok)
                        {
                            Append("✓  卸载完成。", Color.FromArgb(16, 124, 16), true);
                            _headline.Text = "卸载完成";
                            _headline.ForeColor = Color.FromArgb(16, 124, 16);
                        }
                        else
                        {
                            Append("✖  卸载未完全成功：" + err, Color.FromArgb(196, 43, 28), true);
                            _headline.Text = "卸载未完全成功";
                            _headline.ForeColor = Color.FromArgb(196, 43, 28);
                        }
                        _done = true;
                        _busy = false;
                        _btnGo.Text = "关  闭";
                        _btnGo.Enabled = true;
                        _btnCancel.Enabled = true;
                    });
                }
                catch (Exception ex)
                {
                    Safe(delegate
                    {
                        Append("✖  异常：" + Net.Root(ex), Color.FromArgb(196, 43, 28), true);
                        _done = true;
                        _busy = false;
                        _btnGo.Text = "关  闭";
                        _btnGo.Enabled = true;
                        _btnCancel.Enabled = true;
                    });
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void SetBar(int v)
        {
            Safe(delegate { if (v >= 0 && v <= 100) _bar.Value = v; });
        }

        private void Append(string s, Color c) { Append(s, c, false); }

        private void Append(string s, Color c, bool bold)
        {
            _log.SelectionStart = _log.TextLength;
            _log.SelectionColor = c;
            _log.SelectionFont = new Font(_log.Font, bold ? FontStyle.Bold : FontStyle.Regular);
            _log.AppendText(s + Environment.NewLine);
            _log.ScrollToCaret();
        }

        private void Safe(MethodInvoker mi)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                if (InvokeRequired) BeginInvoke(mi); else mi();
            }
            catch { }
        }
    }

    internal static class UninstallerProgram
    {
        [STAThread]
        internal static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string selfDir = Path.GetDirectoryName(Application.ExecutablePath);
            if (selfDir != null && selfDir.EndsWith("\\", StringComparison.Ordinal))
                selfDir = selfDir.TrimEnd('\\');

            bool quiet = false;
            // 默认保留 ~/.claude。删除用户的配置、凭据、会话记录是不可逆操作，
            // 必须由调用方显式要求，绝不能作为无人值守路径的默认行为。
            bool keep = true;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "/quiet", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(args[i], "/S", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(args[i], "-quiet", StringComparison.OrdinalIgnoreCase)) quiet = true;
                if (string.Equals(args[i], "/deleteconfig", StringComparison.OrdinalIgnoreCase)) keep = false;
            }

            if (quiet)
            {
                string err;
                bool ok = InstallerCore.Uninstall(selfDir, null, keep, out err);
                return ok ? 0 : 1;
            }

            Application.Run(new UninstallerForm(selfDir));
            return 0;
        }
    }
}
