using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("音乐批量下载")]
[assembly: AssemblyDescription("通过 Chrome 搜索歌曲并下载公开试听 MP3。编辑者：YRT-GPT")]
[assembly: AssemblyCompany("YRT-GPT")]
[assembly: AssemblyProduct("YRT-GPT 音乐批量下载")]
[assembly: AssemblyCopyright("© 2026 YRT-GPT")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyMetadata("ReleaseDate", YrtMusic.ReleaseInfo.Date)]
[assembly: AssemblyMetadata("ReleaseDateTimeZone", YrtMusic.ReleaseInfo.TimeZone)]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace YrtMusic
{
    internal static class Program
    {
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

        [STAThread]
        public static void Main(string[] args)
        {
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string dataDir = Argument(args, "--data-dir=", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data"));
            string outputDir = Argument(args, "--output-dir=", SongTools.DefaultOutputDirectory());
            try
            {
                Directory.CreateDirectory(dataDir);
                Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
                {
                    MessageBox.Show(e.Exception.Message, "YRT-GPT · 运行提示", MessageBoxButtons.OK, MessageBoxIcon.Error);
                };
                using (MainForm form = new MainForm(dataDir, outputDir)) Application.Run(form);
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动失败：" + ex.Message + "\r\n请把整个程序文件夹解压到可写位置后重试。", "YRT-GPT", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string Argument(string[] args, string prefix, string fallback)
        {
            string value = args.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return value == null ? fallback : value.Substring(prefix.Length).Trim('"');
        }
    }

    public sealed class MainForm : Form
    {
        private static readonly Color Ink = Color.FromArgb(26, 45, 39);
        private static readonly Color Muted = Color.FromArgb(86, 105, 96);
        private static readonly Color Green = Color.FromArgb(15, 113, 82);
        private static readonly Color Pale = Color.FromArgb(235, 246, 239);
        private static readonly Color Amber = Color.FromArgb(145, 81, 14);
        private readonly string dataDir;
        private readonly string outputDir;
        private readonly BatchRunner runner;
        private readonly BindingList<MusicJob> jobs = new BindingList<MusicJob>();
        private readonly Dictionary<Control, FontRole> fontRoles = new Dictionary<Control, FontRole>();
        private readonly List<Button> buttons = new List<Button>();
        private readonly TextBox songs = new TextBox();
        private readonly TextBox log = new TextBox();
        private readonly DataGridView grid = new DataGridView();
        private readonly ComboBox reading = new ComboBox();
        private readonly Button start = new Button(), stop = new Button(), resume = new Button();
        private readonly Button import = new Button(), browser = new Button(), folder = new Button();
        private readonly Label status = new Label(), statusDetail = new Label(), counts = new Label(), destination = new Label();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly TabControl pages = new TabControl();
        private TableLayoutPanel content;
        private Panel scrollHost;
        private CancellationTokenSource activeCancellation;
        private Task activeTask;
        private Task browserTask;
        private CancellationTokenSource browserCancellation;
        private bool busy;
        private bool browserOpening;
        private bool closingRequested;
        private bool closeWaitStarted;
        private bool finalClose;
        private bool loading;
        private bool disposedRunner;
        private float readingPoints = 14f;
        private int savedJobs;
        private sealed class FontRole { public float Ratio; public bool Bold; }

        public MainForm(string dataDirectory, string outputDirectory)
        {
            dataDir = dataDirectory;
            outputDir = outputDirectory;
            Directory.CreateDirectory(dataDir);
            runner = new BatchRunner(dataDir, outputDir);
            runner.Log += delegate(string text) { Ui(delegate { AppendLog(text); }); };
            runner.JobChanged += delegate(MusicJob job) { Ui(delegate { RefreshJob(job); }); };
            runner.NeedsUser += delegate(string text)
            {
                Ui(delegate
                {
                    if (!busy || closingRequested || activeCancellation == null || activeCancellation.IsCancellationRequested) return;
                    status.Text = "需要你在 Chrome 中操作";
                    status.ForeColor = Amber;
                    statusDetail.Text = text;
                    resume.Enabled = busy && !closingRequested;
                    AppendLog(text);
                });
            };
            BuildUi();
            LoadLocalSettings();
            ApplyReadingSize();
            UpdateControls();
            FormClosing += HandleFormClosing;
            FormClosed += delegate { SaveLocalSettings(); DisposeRunner(); };
        }

        private void BuildUi()
        {
            Text = "音乐批量下载 v1.0 · YRT-GPT";
            BackColor = Color.FromArgb(244, 248, 245);
            ForeColor = Ink;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            Icon = SystemIcons.Application;
            scrollHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(18) };
            Controls.Add(scrollHost);
            MenuStrip menu = new MenuStrip { Name = "MainMenu", Dock = DockStyle.Top, BackColor = Color.White, ForeColor = Ink };
            ToolStripMenuItem helpMenu = new ToolStripMenuItem("帮助(&H)");
            ToolStripMenuItem aboutMenu = new ToolStripMenuItem("关于(&A)") { Name = "AboutMenu" };
            aboutMenu.Click += delegate { ShowAbout(); };
            helpMenu.DropDownItems.Add(aboutMenu);
            menu.Items.Add(helpMenu);
            Role(menu, 1f, false);
            MainMenuStrip = menu;
            Controls.Add(menu);
            content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 6, Margin = Padding.Empty };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            scrollHost.Controls.Add(content);

            TableLayoutPanel header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 12) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Label heading = Label("音乐批量下载", 1.55f, true);
            heading.Margin = new Padding(0, 0, 20, 2);
            header.Controls.Add(heading, 0, 0);
            Label subtitle = Label("粘贴歌单，逐首搜索，试听 MP3 自动保存到桌面。", 0.95f, false);
            subtitle.ForeColor = Muted;
            subtitle.Margin = new Padding(0, 4, 20, 0);
            header.Controls.Add(subtitle, 0, 1);
            FlowLayoutPanel fontTools = Flow();
            fontTools.WrapContents = false;
            fontTools.Margin = new Padding(0, 5, 0, 0);
            Label sizeLabel = Label("字号", 0.9f, false);
            sizeLabel.Margin = new Padding(0, 7, 8, 0);
            fontTools.Controls.Add(sizeLabel);
            reading.Name = "ReadingSize";
            reading.DropDownStyle = ComboBoxStyle.DropDownList;
            reading.Items.AddRange(new object[] { "12 磅", "14 磅", "16 磅" });
            reading.AccessibleName = "界面字号";
            Role(reading, 0.95f, false);
            reading.SelectedIndexChanged += delegate
            {
                if (loading) return;
                readingPoints = reading.SelectedIndex == 0 ? 12f : reading.SelectedIndex == 2 ? 16f : 14f;
                ApplyReadingSize();
                SaveLocalSettings();
            };
            fontTools.Controls.Add(reading);
            header.Controls.Add(fontTools, 1, 0);
            header.SetRowSpan(fontTools, 2);
            content.Controls.Add(header, 0, 0);

            TableLayoutPanel inputCard = Card();
            Label inputTitle = Label("1  粘贴歌名，每行一首", 1.05f, true);
            inputTitle.Margin = new Padding(0, 0, 0, 5);
            inputCard.Controls.Add(inputTitle);
            Label inputHelp = Label("可写“歌名 - 歌手”，更容易匹配。程序会自动选择最匹配的结果。", 0.9f, false);
            inputHelp.ForeColor = Muted;
            inputHelp.Margin = new Padding(0, 0, 0, 9);
            inputCard.Controls.Add(inputHelp);
            songs.Name = "SongInput";
            songs.Multiline = true;
            songs.AcceptsReturn = true;
            songs.AcceptsTab = false;
            songs.ScrollBars = ScrollBars.Vertical;
            songs.WordWrap = false;
            songs.Dock = DockStyle.Top;
            songs.BorderStyle = BorderStyle.FixedSingle;
            songs.AccessibleName = "歌名单，每行一首";
            songs.Margin = new Padding(0, 0, 0, 4);
            Role(songs, 1f, false);
            inputCard.Controls.Add(songs);
            content.Controls.Add(inputCard, 0, 1);

            FlowLayoutPanel actions = Flow();
            actions.Dock = DockStyle.Top;
            actions.Margin = new Padding(0, 10, 0, 6);
            ConfigureButton(start, "开始搜索并下载", true, async delegate { await StartBatchAsync(); });
            ConfigureButton(stop, "停止", false, delegate { RequestStop(); });
            ConfigureButton(resume, "继续任务", false, delegate
            {
                if (!busy || closingRequested || activeCancellation == null || activeCancellation.IsCancellationRequested || !runner.IsPaused) return;
                runner.Continue();
                resume.Enabled = false;
                status.Text = "继续搜索与下载";
                status.ForeColor = Green;
                statusDetail.Text = "已继续任务，请保持程序和 Chrome 开启。";
            });
            ConfigureButton(import, "导入 TXT 歌单", false, delegate { ImportText(); });
            ConfigureButton(browser, "打开 Chrome", false, async delegate { await OpenBrowserAsync(); });
            ConfigureButton(folder, "打开音乐文件夹", false, delegate { OpenOutputFolder(); });
            actions.Controls.AddRange(new Control[] { start, stop, resume, import, browser, folder });
            content.Controls.Add(actions, 0, 2);

            TableLayoutPanel stateCard = Card();
            stateCard.BackColor = Pale;
            status.Text = "准备就绪";
            status.ForeColor = Green;
            status.Name = "RunStatus";
            Role(status, 1.05f, true);
            SetupWrap(status);
            stateCard.Controls.Add(status);
            statusDetail.Text = "输入歌名后点击“开始搜索并下载”。";
            statusDetail.Name = "RunDetail";
            statusDetail.ForeColor = Muted;
            Role(statusDetail, 0.9f, false);
            SetupWrap(statusDetail);
            statusDetail.Margin = new Padding(0, 5, 0, 4);
            stateCard.Controls.Add(statusDetail);
            TableLayoutPanel progressRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 5, 0, 1) };
            progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            progress.Dock = DockStyle.Fill;
            progress.Style = ProgressBarStyle.Continuous;
            progress.Margin = new Padding(0, 4, 16, 4);
            progressRow.Controls.Add(progress, 0, 0);
            counts.Text = "尚未开始";
            Role(counts, 0.9f, false);
            SetupWrap(counts);
            counts.Margin = Padding.Empty;
            progressRow.Controls.Add(counts, 1, 0);
            stateCard.Controls.Add(progressRow);
            destination.Text = "保存位置：" + outputDir;
            destination.Name = "OutputDirectory";
            destination.ForeColor = Muted;
            Role(destination, 0.85f, false);
            SetupWrap(destination);
            destination.Margin = new Padding(0, 6, 0, 0);
            stateCard.Controls.Add(destination);
            content.Controls.Add(stateCard, 0, 3);

            pages.Name = "MainTabs";
            pages.Dock = DockStyle.Top;
            pages.Margin = new Padding(0, 12, 0, 4);
            Role(pages, 0.95f, false);
            TabPage taskPage = new TabPage("下载任务"), logPage = new TabPage("运行日志");
            taskPage.BackColor = logPage.BackColor = Color.White;
            taskPage.Padding = logPage.Padding = new Padding(8);
            pages.TabPages.AddRange(new[] { taskPage, logPage });
            ConfigureGrid();
            taskPage.Controls.Add(grid);
            ConfigureReadOnly(log, "RunLog");
            logPage.Controls.Add(log);
            content.Controls.Add(pages, 0, 4);

            Label footer = Label("YRT-GPT  ·  v1.0  |  下载期间保持 Chrome 开启；遇到验证，完成后点击“继续任务”。", 0.85f, false);
            footer.ForeColor = Muted;
            footer.Margin = new Padding(0, 6, 0, 2);
            content.Controls.Add(footer, 0, 5);
            content.SizeChanged += delegate { UpdateWrapWidths(content); };
        }

        private void ConfigureGrid()
        {
            grid.Name = "DownloadTasks";
            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AutoGenerateColumns = false;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.BackgroundColor = Color.White;
            grid.BorderStyle = BorderStyle.None;
            grid.GridColor = Color.FromArgb(223, 232, 226);
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Pale;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Ink;
            grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            grid.DefaultCellStyle.Padding = new Padding(7, 5, 7, 5);
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(210, 235, 220);
            grid.DefaultCellStyle.SelectionForeColor = Ink;
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 251, 249);
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            Role(grid, 0.9f, false);
            AddColumn("Index", "序号", 45, 65);
            AddColumn("Query", "输入歌名", 170, 140);
            AddColumn("Title", "匹配歌曲", 170, 140);
            AddColumn("Artist", "歌手", 110, 100);
            AddColumn("State", "状态", 95, 95);
            AddColumn("Detail", "说明", 230, 160);
            grid.DataSource = jobs;
            grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex < 0 || e.RowIndex >= jobs.Count) return;
                string path = jobs[e.RowIndex].FilePath;
                if (!String.IsNullOrWhiteSpace(path) && File.Exists(path))
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
            };
        }

        private void AddColumn(string property, string caption, int weight, int minimumWidth)
        {
            DataGridViewTextBoxColumn column = new DataGridViewTextBoxColumn
            {
                DataPropertyName = property, Name = property, HeaderText = caption,
                FillWeight = weight, MinimumWidth = minimumWidth, SortMode = DataGridViewColumnSortMode.NotSortable
            };
            if (property == "Index") { column.AutoSizeMode = DataGridViewAutoSizeColumnMode.None; column.Width = minimumWidth; }
            grid.Columns.Add(column);
        }

        private async Task StartBatchAsync()
        {
            if (busy || browserOpening || closingRequested) return;
            List<MusicJob> parsed = SongTools.ParseLines(songs.Text);
            if (parsed == null || parsed.Count == 0)
            {
                status.Text = "请先填写歌名";
                statusDetail.Text = "每行输入一首歌，也可以从 TXT 文件导入。";
                songs.Focus();
                return;
            }
            SaveLocalSettings();
            jobs.RaiseListChangedEvents = false;
            jobs.Clear();
            foreach (MusicJob job in parsed) jobs.Add(job);
            jobs.RaiseListChangedEvents = true;
            jobs.ResetBindings();
            savedJobs = 0;
            progress.Maximum = Math.Max(1, jobs.Count);
            progress.Value = 0;
            counts.Text = "已保存 0 / " + jobs.Count + " 首";
            busy = true;
            status.Text = "正在搜索与下载";
            status.ForeColor = Green;
            statusDetail.Text = "程序将按歌单顺序搜索，并自动下载最匹配结果的试听 MP3。";
            activeCancellation = new CancellationTokenSource();
            UpdateControls();
            AppendLog("开始本批任务，共 " + jobs.Count + " 首。保存位置：" + outputDir);
            try
            {
                activeTask = runner.RunAsync(parsed, activeCancellation.Token);
                await activeTask;
                status.Text = activeCancellation.IsCancellationRequested ? "任务已停止" : "本批任务已结束";
                status.ForeColor = Green;
                statusDetail.Text = activeCancellation.IsCancellationRequested
                    ? "已停止后续搜索与下载，已保存的音乐保留在音乐文件夹。"
                    : "已保存 " + savedJobs + " 首，共 " + jobs.Count + " 首。可在下方查看每首歌的结果。";
            }
            catch (OperationCanceledException)
            {
                status.Text = "任务已停止";
                status.ForeColor = Green;
                statusDetail.Text = "已保存的音乐保留在音乐文件夹。";
                AppendLog("任务已停止。");
            }
            catch (Exception ex)
            {
                status.Text = "本批任务中断";
                status.ForeColor = Amber;
                statusDetail.Text = ex.Message;
                AppendLog("任务中断：" + ex.Message);
            }
            finally
            {
                activeTask = null;
                if (activeCancellation != null) { activeCancellation.Dispose(); activeCancellation = null; }
                busy = false;
                UpdateControls();
                SaveLocalSettings();
            }
        }

        private void RequestStop()
        {
            if (activeCancellation == null || activeCancellation.IsCancellationRequested) return;
            activeCancellation.Cancel();
            stop.Enabled = resume.Enabled = false;
            status.Text = "正在停止任务";
            statusDetail.Text = "正在取消当前操作，请稍候。已保存的音乐会保留。";
            AppendLog("已请求停止任务。");
        }

        private async Task OpenBrowserAsync()
        {
            if (busy || browserOpening || closingRequested) return;
            browserOpening = true;
            browserCancellation = new CancellationTokenSource();
            status.Text = "正在打开 Chrome";
            statusDetail.Text = "正在准备歌曲网站，请稍候。";
            UpdateControls();
            try
            {
                browserTask = runner.OpenBrowserAsync(browserCancellation.Token);
                await browserTask;
                status.Text = "Chrome 已就绪";
                status.ForeColor = Green;
                statusDetail.Text = "现在可以点击“开始搜索并下载”。";
            }
            catch (OperationCanceledException) { AppendLog("已取消打开 Chrome。"); }
            catch (Exception ex)
            {
                status.Text = "打开 Chrome 未完成";
                status.ForeColor = Amber;
                statusDetail.Text = ex.Message;
                AppendLog("Chrome：" + ex.Message);
            }
            finally
            {
                browserTask = null;
                browserOpening = false;
                if (browserCancellation != null) { browserCancellation.Dispose(); browserCancellation = null; }
                UpdateControls();
            }
        }

        private void ImportText()
        {
            using (OpenFileDialog picker = new OpenFileDialog())
            {
                picker.Title = "导入歌单：每行一首歌";
                picker.Filter = "文本歌单 (*.txt)|*.txt|所有文件 (*.*)|*.*";
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    songs.Text = ReadTextFile(picker.FileName);
                    status.Text = "歌单已导入";
                    statusDetail.Text = "检查歌名后点击“开始搜索并下载”。";
                    SaveLocalSettings();
                }
                catch (Exception ex) { MessageBox.Show(this, "无法读取歌单：" + ex.Message, "导入 TXT", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        private static string ReadTextFile(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 2 && ((bytes[0] == 0xff && bytes[1] == 0xfe) || (bytes[0] == 0xfe && bytes[1] == 0xff))) return File.ReadAllText(path, Encoding.Unicode);
            try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\ufeff'); }
            catch (DecoderFallbackException) { return Encoding.GetEncoding(936).GetString(bytes); }
        }

        private void OpenOutputFolder()
        {
            try { Directory.CreateDirectory(outputDir); Process.Start(new ProcessStartInfo(outputDir) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, "无法打开音乐文件夹：" + ex.Message, "YRT-GPT", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void RefreshJob(MusicJob job)
        {
            if (IsDisposed || Disposing) return;
            for (int i = 0; i < jobs.Count; i++)
            {
                if (Object.ReferenceEquals(jobs[i], job) || jobs[i].Index == job.Index)
                {
                    if (!Object.ReferenceEquals(jobs[i], job)) jobs[i] = job;
                    jobs.ResetItem(i);
                    break;
                }
            }
            savedJobs = jobs.Count(x => !String.IsNullOrWhiteSpace(x.FilePath));
            progress.Value = Math.Min(progress.Maximum, savedJobs);
            counts.Text = "已保存 " + savedJobs + " / " + jobs.Count + " 首";
            if (busy && !runner.IsPaused && !closingRequested && activeCancellation != null && !activeCancellation.IsCancellationRequested)
            {
                status.Text = "正在搜索与下载";
                status.ForeColor = Green;
                statusDetail.Text = "第 " + job.Index + " 首：" + job.Query + "　" + job.State;
            }
        }

        private void AppendLog(string text)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text;
            if (log.TextLength > 180000) log.Text = log.Text.Substring(log.TextLength - 120000);
            log.AppendText(line + Environment.NewLine);
            try { File.AppendAllText(Path.Combine(dataDir, "界面运行日志.txt"), DateTimeOffset.Now.ToString("o") + " " + text + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }

        private void LoadLocalSettings()
        {
            loading = true;
            try
            {
                string sizeFile = Path.Combine(dataDir, "字号.txt");
                float selected;
                if (File.Exists(sizeFile) && Single.TryParse(File.ReadAllText(sizeFile).Trim(), out selected) && (selected == 12f || selected == 14f || selected == 16f)) readingPoints = selected;
                string inputFile = Path.Combine(dataDir, "歌单.txt");
                if (File.Exists(inputFile)) songs.Text = File.ReadAllText(inputFile, Encoding.UTF8);
            }
            catch (Exception ex) { AppendLog("读取上次设置未完成：" + ex.Message); }
            reading.SelectedIndex = readingPoints == 12f ? 0 : readingPoints == 16f ? 2 : 1;
            loading = false;
        }

        private void SaveLocalSettings()
        {
            try
            {
                Directory.CreateDirectory(dataDir);
                File.WriteAllText(Path.Combine(dataDir, "字号.txt"), readingPoints.ToString("0", System.Globalization.CultureInfo.InvariantCulture), Encoding.UTF8);
                File.WriteAllText(Path.Combine(dataDir, "歌单.txt"), songs.Text, Encoding.UTF8);
            }
            catch { }
        }

        private void ShowAbout()
        {
            using (Font aboutFont = new Font("Microsoft YaHei UI", readingPoints, FontStyle.Regular, GraphicsUnit.Point))
            using (Form dialog = new Form())
            {
                dialog.Name = "AboutDialog";
                dialog.Text = "关于";
                dialog.Font = aboutFont;
                dialog.AutoScaleMode = AutoScaleMode.None;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.BackColor = Color.White;
                dialog.ForeColor = Ink;
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.Padding = new Padding(24);
                Rectangle area = Screen.FromControl(this).WorkingArea;
                int line = aboutFont.Height;
                dialog.ClientSize = new Size(Math.Min(area.Width - 60, Math.Max(640, line * 30)), Math.Min(area.Height - 80, Math.Max(340, line * 11)));
                TextBox information = new TextBox
                {
                    Name = "AboutInformation", Dock = DockStyle.Fill, ReadOnly = true,
                    Multiline = true, WordWrap = true, ScrollBars = ScrollBars.Vertical,
                    BackColor = Color.White, ForeColor = Ink, BorderStyle = BorderStyle.None,
                    Text = "音乐批量下载 v1.0\r\n作者：YRT-GPT\r\n发布日期：" + ReleaseInfo.Date + "\r\n\r\n粘贴多行歌名，逐项搜索并下载网站提供的试听MP3。"
                };
                FlowLayoutPanel actions = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom, AutoSize = true, WrapContents = false,
                    FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 16, 0, 0)
                };
                Button close = new Button { Name = "CloseAbout", Text = "关闭", AutoSize = true, DialogResult = DialogResult.OK, Padding = new Padding(12, 4, 12, 4) };
                actions.Controls.Add(close);
                dialog.Controls.Add(information);
                dialog.Controls.Add(actions);
                dialog.AcceptButton = close;
                dialog.CancelButton = close;
                dialog.ActiveControl = close;
                dialog.Shown += delegate
                {
                    information.SelectionStart = 0;
                    information.SelectionLength = 0;
                    close.Select();
                };
                dialog.ShowDialog(this);
            }
        }

        private async void HandleFormClosing(object sender, FormClosingEventArgs e)
        {
            if (finalClose) return;
            if (!busy && !browserOpening) return;
            e.Cancel = true;
            if (closeWaitStarted) return;
            closeWaitStarted = true;
            closingRequested = true;
            SaveLocalSettings();
            status.Text = "正在停止任务并退出";
            statusDetail.Text = "请稍候，正在结束本程序当前操作。已保存的音乐会保留。";
            UpdateControls();
            Task waitBatch = activeTask;
            Task waitBrowser = browserTask;
            if (activeCancellation != null) activeCancellation.Cancel();
            if (browserCancellation != null) browserCancellation.Cancel();
            try { if (waitBatch != null) await waitBatch; } catch { }
            try { if (waitBrowser != null) await waitBrowser; } catch { }
            finalClose = true;
            if (!IsDisposed) Close();
        }

        private void DisposeRunner()
        {
            if (disposedRunner) return;
            disposedRunner = true;
            runner.Dispose();
        }

        private void UpdateControls()
        {
            bool idle = !busy && !browserOpening && !closingRequested;
            start.Enabled = idle;
            import.Enabled = idle;
            songs.ReadOnly = !idle;
            browser.Enabled = idle;
            stop.Enabled = busy && !closingRequested && activeCancellation != null && !activeCancellation.IsCancellationRequested;
            resume.Enabled = busy && !closingRequested && activeCancellation != null && !activeCancellation.IsCancellationRequested && runner.IsPaused;
        }

        private void Ui(Action action)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            if (!InvokeRequired) { action(); return; }
            try { BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        private void ApplyReadingSize()
        {
            SuspendLayout();
            try
            {
                foreach (KeyValuePair<Control, FontRole> item in fontRoles)
                    item.Key.Font = new Font("Microsoft YaHei UI", readingPoints * Math.Max(1f, item.Value.Ratio), item.Value.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
                Font = new Font("Microsoft YaHei UI", readingPoints, FontStyle.Regular, GraphicsUnit.Point);
                int line = Font.Height;
                reading.Width = TextRenderer.MeasureText("16 磅  ", reading.Font).Width + 34;
                songs.Height = line * 4 + 22;
                foreach (Button button in buttons)
                {
                    button.MinimumSize = Size.Empty;
                    button.MaximumSize = Size.Empty;
                    Size text = TextRenderer.MeasureText(button.Text, button.Font);
                    button.Size = new Size(text.Width + 36, Math.Max(line + 22, text.Height + 20));
                    button.MinimumSize = button.Size;
                    button.MaximumSize = button.Size;
                }
                grid.RowTemplate.Height = grid.Font.Height + 22;
                foreach (DataGridViewColumn column in grid.Columns)
                {
                    int headerWidth = TextRenderer.MeasureText(column.HeaderText, grid.Font).Width + 28;
                    column.MinimumWidth = Math.Max(column.MinimumWidth, headerWidth);
                    if (column.Name == "Index") column.Width = headerWidth;
                }
                grid.ColumnHeadersHeight = grid.Font.Height + 24;
                grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
                grid.AutoResizeRows(DataGridViewAutoSizeRowsMode.AllCells);
                pages.ItemSize = new Size(0, pages.Font.Height + 18);
                pages.Padding = new Point(16, 5);
                pages.Height = line * 9 + 90;
                progress.Height = Math.Max(12, line / 2);
                Rectangle area = Screen.FromControl(this).WorkingArea;
                Size frame = SizeFromClientSize(Size.Empty);
                int targetWidth = Math.Min(area.Width - frame.Width - 24, Math.Max(1020, line * 50));
                int targetHeight = Math.Min(area.Height - frame.Height - 24, Math.Max(740, line * 35));
                MinimumSize = Size.Empty;
                ClientSize = new Size(targetWidth, targetHeight);
                MinimumSize = new Size(Math.Min(area.Width - 24, Math.Max(880, line * 31)), Math.Min(area.Height - 24, Math.Max(650, line * 24)));
                MaximumSize = new Size(0, 0);
                UpdateWrapWidths(content);
                scrollHost.AutoScrollPosition = Point.Empty;
            }
            finally { ResumeLayout(true); }
        }

        private void UpdateWrapWidths(Control parent)
        {
            if (parent == null || parent.IsDisposed) return;
            foreach (Control child in parent.Controls)
            {
                Label label = child as Label;
                if (label != null && label.AutoSize && label.Dock == DockStyle.Top)
                {
                    int width = Math.Max(100, parent.ClientSize.Width - parent.Padding.Horizontal - label.Margin.Horizontal - 6);
                    if (label.MaximumSize.Width != width) label.MaximumSize = new Size(width, 0);
                }
                if (child.HasChildren) UpdateWrapWidths(child);
            }
        }

        private Label Label(string text, float ratio, bool bold)
        {
            Label result = new Label { Text = text };
            SetupWrap(result);
            Role(result, ratio, bold);
            return result;
        }

        private static void SetupWrap(Label label)
        {
            label.AutoSize = true;
            label.Dock = DockStyle.Top;
            label.UseMnemonic = false;
            label.Margin = Padding.Empty;
        }

        private void ConfigureReadOnly(TextBox text, string name)
        {
            text.Name = name;
            text.Dock = DockStyle.Fill;
            text.Multiline = true;
            text.ReadOnly = true;
            text.ScrollBars = ScrollBars.Vertical;
            text.WordWrap = true;
            text.BackColor = Color.White;
            text.BorderStyle = BorderStyle.None;
            Role(text, 0.95f, false);
        }

        private TableLayoutPanel Card()
        {
            TableLayoutPanel card = new TableLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.White, ColumnCount = 1, Padding = new Padding(16, 12, 16, 12), Margin = Padding.Empty
            };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            card.SizeChanged += delegate { UpdateWrapWidths(card); };
            return card;
        }

        private static FlowLayoutPanel Flow()
        {
            return new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty, Padding = Padding.Empty };
        }

        private void Role(Control control, float ratio, bool bold)
        {
            fontRoles[control] = new FontRole { Ratio = ratio, Bold = bold };
        }

        private void ConfigureButton(Button button, string text, bool primary, EventHandler handler)
        {
            button.Text = text;
            button.UseMnemonic = false;
            button.AutoSize = false;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = primary ? Green : Color.FromArgb(184, 207, 193);
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.BackColor = primary ? Green : Color.White;
            button.ForeColor = primary ? Color.White : Green;
            button.Margin = new Padding(0, 0, 10, 9);
            button.Padding = Padding.Empty;
            button.Cursor = Cursors.Hand;
            button.Click += handler;
            Role(button, 0.95f, primary);
            buttons.Add(button);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeRunner();
            base.Dispose(disposing);
        }
    }
}
