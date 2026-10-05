using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AzureArchive.RevisionCompare.Installation
{
    internal sealed class InstallerWindow : Form
    {
        internal const string WindowTitle = "剧本改稿同步 MOD  一键安装";
        static readonly Color Blue = Color.FromArgb(28, 135, 179);
        static readonly Color Ink = Color.FromArgb(37, 69, 87);
        static readonly Color Muted = Color.FromArgb(95, 117, 130);
        static readonly Color Red = Color.FromArgb(188, 48, 61);
        readonly TextBox location = new TextBox();
        readonly TextBox details = new TextBox();
        readonly Button browse = new Button();
        readonly Button install = new Button();
        readonly Button uninstall = new Button();
        readonly Button close = new Button();
        readonly Label status = new Label();
        readonly Label pathHint = new Label();
        readonly ProgressBar progress = new ProgressBar();
        bool busy;

        public InstallerWindow(string initial)
        {
            SuspendLayout();
            Text = WindowTitle;
            Name = "RevisionCompareInstallerWindow";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(940, 700);
            MinimumSize = new Size(820, 640);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 10F);
            BackColor = Color.FromArgb(247, 251, 253);
            ForeColor = Ink;
            DoubleBuffered = true;
            MaximizeBox = false;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, AutoScroll = true,
                Padding = new Padding(28, 22, 28, 20), BackColor = BackColor
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int row = 0; row < 8; row++)
                layout.RowStyles.Add(new RowStyle(row == 4 ? SizeType.Percent : SizeType.AutoSize, row == 4 ? 100 : 0));

            var title = TextLabel("InstallerTitle", WindowTitle, 24F, FontStyle.Bold, Ink);
            title.Margin = new Padding(0, 0, 0, 4);
            layout.Controls.Add(title, 0, 0);
            var subtitle = TextLabel("InstallerSubtitle", "V1.1  ·  正式版  ·  支持自动检查与直接更新", 10.5F, FontStyle.Regular, Blue);
            subtitle.Margin = new Padding(2, 0, 0, 20);
            layout.Controls.Add(subtitle, 0, 1);
            layout.Controls.Add(CreatePathCard(initial), 0, 2);
            layout.Controls.Add(CreateFeatureCard(), 0, 3);
            layout.Controls.Add(CreateStatusCard(), 0, 4);

            progress.Name = "InstallProgress";
            progress.Dock = DockStyle.Fill;
            progress.Height = 10;
            progress.Margin = new Padding(0, 10, 0, 13);
            progress.Style = ProgressBarStyle.Continuous;
            progress.MarqueeAnimationSpeed = 28;
            layout.Controls.Add(progress, 0, 5);
            layout.Controls.Add(CreateActions(), 0, 6);
            var footer = TextLabel("InstallerFooter", "安装完成后可启动 AA。需要修复或卸载时，再次运行此安装器。", 9F, FontStyle.Regular, Muted);
            footer.Margin = new Padding(0, 14, 0, 0);
            layout.Controls.Add(footer, 0, 7);
            Controls.Add(layout);

            AcceptButton = install;
            CancelButton = close;
            browse.Click += Browse;
            install.Click += delegate { RunOperation(false); };
            uninstall.Click += delegate { RunOperation(true); };
            close.Click += delegate { if (!busy) Close(); };
            location.TextChanged += delegate { if (!busy) ResetReady(); };
            FormClosing += GuardClosing;
            ResetReady();
            ResumeLayout(true);
        }

        Control CreatePathCard(string initial)
        {
            var card = Card("DirectoryCard", 3);
            var heading = TextLabel("DirectoryHeading", "AA 根目录", 11F, FontStyle.Bold, Ink);
            heading.Margin = new Padding(0, 0, 0, 9);
            card.Controls.Add(heading, 0, 0);
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            location.Name = "RootPath";
            location.Text = initial ?? "";
            location.Dock = DockStyle.Fill;
            location.Margin = new Padding(0, 4, 12, 0);
            location.TabIndex = 0;
            StyleButton(browse, "BrowseDirectory", "选择目录…", false, 118);
            browse.Size = browse.MinimumSize = new Size(118, 36);
            browse.TabIndex = 1;
            browse.Margin = Padding.Empty;
            row.Controls.Add(location, 0, 0);
            row.Controls.Add(browse, 1, 0);
            card.Controls.Add(row, 0, 1);
            pathHint.Name = "DirectoryHint";
            pathHint.AutoSize = true;
            pathHint.Dock = DockStyle.Fill;
            pathHint.ForeColor = Muted;
            pathHint.Margin = new Padding(0, 9, 0, 0);
            card.Controls.Add(pathHint, 0, 2);
            return card;
        }

        Control CreateFeatureCard()
        {
            var card = Card("FeaturesCard", 2);
            var heading = TextLabel("FeaturesHeading", "这个 MOD 用来做什么？", 11F, FontStyle.Bold, Ink);
            heading.Margin = new Padding(0, 0, 0, 7);
            card.Controls.Add(heading, 0, 0);
            var description = TextLabel("FeaturesDescription", "修改剧情时，随时对照改稿前后的效果。\r\n自动保留每条对白最近两次不同的播放版本，可在小窗重播上一版。\r\n改错时一键恢复这一条对白的完整设置，支持撤销和重做，不影响其他对白。", 10F, FontStyle.Regular, Muted);
            card.Controls.Add(description, 0, 1);
            return card;
        }

        Control CreateStatusCard()
        {
            var card = Card("ResultCard", 2);
            card.AutoSize = false;
            card.MinimumSize = new Size(0, 126);
            card.Margin = Padding.Empty;
            card.RowStyles.Clear();
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            status.Name = "SetupStatus";
            status.AutoSize = true;
            status.Dock = DockStyle.Fill;
            status.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
            status.Margin = new Padding(0, 0, 0, 10);
            status.ForeColor = Blue;
            card.Controls.Add(status, 0, 0);
            details.Name = "OperationDetails";
            details.Multiline = true;
            details.ReadOnly = true;
            details.ScrollBars = ScrollBars.Vertical;
            details.BorderStyle = BorderStyle.None;
            details.BackColor = Color.White;
            details.ForeColor = Ink;
            details.Dock = DockStyle.Fill;
            details.Margin = Padding.Empty;
            details.WordWrap = true;
            details.TabIndex = 5;
            card.Controls.Add(details, 0, 1);
            return card;
        }

        Control CreateActions()
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
            StyleButton(install, "StartInstall", "一键安装 / 修复", true, 198);
            StyleButton(uninstall, "UninstallMod", "卸载此 MOD", false, 150);
            StyleButton(close, "CloseInstaller", "关闭", false, 106);
            install.TabIndex = 2; uninstall.TabIndex = 3; close.TabIndex = 4;
            install.Margin = new Padding(0, 0, 12, 0);
            uninstall.Margin = Padding.Empty;
            close.Margin = Padding.Empty;
            close.DialogResult = DialogResult.Cancel;
            actions.Controls.Add(install); actions.Controls.Add(uninstall);
            row.Controls.Add(actions, 0, 0); row.Controls.Add(close, 1, 0);
            return row;
        }

        TableLayoutPanel Card(string name, int rows)
        {
            var card = new TableLayoutPanel
            {
                Name = name, Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = rows,
                BackColor = Color.White, Padding = new Padding(18, 12, 18, 12), Margin = new Padding(0, 0, 0, 12)
            };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int row = 0; row < rows; row++) card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            return card;
        }

        Label TextLabel(string name, string text, float size, FontStyle style, Color color)
        {
            return new Label
            {
                Name = name, Text = text, AutoSize = true, Dock = DockStyle.Fill,
                Font = new Font(Font.FontFamily, size, style), ForeColor = color, Margin = Padding.Empty,
                UseMnemonic = false
            };
        }

        void StyleButton(Button button, string name, string text, bool primary, int width)
        {
            button.Name = name; button.Text = text;
            button.Size = new Size(width, 42);
            button.MinimumSize = new Size(width, 42);
            button.AutoSize = true;
            button.Padding = new Padding(12, 4, 12, 4);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(190, 210, 222);
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(22, 117, 158) : Color.FromArgb(237, 247, 252);
            button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(18, 99, 135) : Color.FromArgb(224, 240, 248);
            button.BackColor = primary ? Blue : Color.White;
            button.ForeColor = primary ? Color.White : Ink;
            button.UseVisualStyleBackColor = false;
            button.Font = new Font(Font.FontFamily, 10F, primary ? FontStyle.Bold : FontStyle.Regular);
        }

        void ResetReady()
        {
            bool selected = !String.IsNullOrWhiteSpace(location.Text);
            install.Enabled = uninstall.Enabled = selected;
            status.ForeColor = Blue;
            status.Text = selected ? "准备安装" : "请选择 AA 目录";
            pathHint.Text = "选择包含 AzureArchive.exe 的文件夹；安装时将检查 AA 与当前配置。";
            details.Text = "安装前请关闭 AA。\r\n点击“一键安装 / 修复”后，将在当前 AA 配置中启用本 MOD。";
            progress.Value = 0;
        }

        void Browse(object sender, EventArgs e)
        {
            if (busy) return;
            using (var picker = new FolderBrowserDialog
            {
                Description = "选择包含 AzureArchive.exe 的 AA 根目录",
                SelectedPath = Directory.Exists(location.Text) ? location.Text : "",
                ShowNewFolderButton = false
            })
            {
                if (picker.ShowDialog(this) == DialogResult.OK) location.Text = picker.SelectedPath;
            }
        }

        async void RunOperation(bool remove)
        {
            if (busy) return;
            string selected = location.Text.Trim();
            if (String.IsNullOrWhiteSpace(selected)) { ResetReady(); location.Focus(); return; }
            if (remove && MessageBox.Show(this,
                "确认卸载此 AA 目录中的剧本改稿同步 MOD？\r\n\r\n" + selected +
                "\r\n\r\n将按安装记录移除本 MOD；其他 MOD 和用户修改过的内容会保留。",
                "确认卸载", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            SetBusy(true);
            status.ForeColor = Blue;
            status.Text = remove ? "正在卸载…" : "正在安装 / 修复…";
            details.Text = "正在检查并处理所选目录，请稍候。\r\n" + selected;
            try
            {
                OperationResult result = await Task.Run(delegate
                {
                    return remove ? InstallerCore.Uninstall(selected) :
                        InstallerCore.Install(selected, Program.Resource("revisioncompare.plugin"), Program.Resource("revisioncompare.manifest"));
                });
                if (IsDisposed) return;
                if (result == null) throw new InvalidOperationException("安装程序未返回处理结果，请重新运行安装器检查。");
                status.Text = result.Preserved.Count > 0 ? "处理完成，请查看保留内容" : remove ? "卸载处理完成" : "安装完成";
                status.ForeColor = result.Preserved.Count > 0 ? Color.FromArgb(157, 106, 22) : Color.FromArgb(38, 109, 79);
                details.Text = result.ToString();
                details.SelectionStart = 0; details.SelectionLength = 0; details.ScrollToCaret();
                progress.Style = ProgressBarStyle.Continuous; progress.Value = 100;
            }
            catch (Exception error)
            {
                if (IsDisposed) return;
                status.Text = "操作未完成";
                status.ForeColor = Red;
                details.Text = error.Message;
                details.SelectionStart = 0; details.SelectionLength = 0; details.ScrollToCaret();
                progress.Style = ProgressBarStyle.Continuous; progress.Value = 0;
            }
            finally { if (!IsDisposed) SetBusy(false); }
        }

        void SetBusy(bool value)
        {
            busy = value;
            location.ReadOnly = value;
            location.Enabled = browse.Enabled = close.Enabled = !value;
            install.Enabled = uninstall.Enabled = !value && !String.IsNullOrWhiteSpace(location.Text);
            UseWaitCursor = value;
            if (value) { progress.Value = 0; progress.Style = ProgressBarStyle.Marquee; }
            else if (progress.Style == ProgressBarStyle.Marquee) progress.Style = ProgressBarStyle.Continuous;
        }

        void GuardClosing(object sender, FormClosingEventArgs e)
        {
            if (!busy) return;
            e.Cancel = true;
            status.Text = "正在处理，请在操作完成后关闭安装器。";
        }
    }
}
