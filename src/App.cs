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
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("音乐下载")]
[assembly: AssemblyDescription("在 Chrome 中搜索公开歌曲，选择结果后下载试听 MP3。编辑者：YRT-GPT")]
[assembly: AssemblyCompany("YRT-GPT")]
[assembly: AssemblyProduct("YRT-GPT 音乐下载")]
[assembly: AssemblyCopyright("© 2026 YRT-GPT")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]
[assembly: AssemblyMetadata("ReleaseDate", YrtMusic.ReleaseInfo.Date)]
[assembly: AssemblyMetadata("ReleaseDateTimeZone", YrtMusic.ReleaseInfo.TimeZone)]
[assembly: AssemblyMetadata("PublicationStatus", YrtMusic.ReleaseInfo.Status)]

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
            try
            {
                string data = Argument(args, "--data-dir=", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data"));
                string output = Argument(args, "--output-dir=", null);
                Directory.CreateDirectory(data);
                Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
                { MessageBox.Show(e.Exception.Message, "YRT-GPT · 运行提示", MessageBoxButtons.OK, MessageBoxIcon.Error); };
                using (MainForm form = new MainForm(data, output)) Application.Run(form);
            }
            catch (Exception ex)
            { MessageBox.Show("启动失败：" + ex.Message + "\r\n请将整个程序文件夹解压到可写位置后重试。", "YRT-GPT", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        private static string Argument(string[] args, string prefix, string fallback)
        {
            string value = args.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return value == null ? fallback : value.Substring(prefix.Length).Trim('"');
        }
    }

    internal sealed class SavedSettings
    {
        public int ReadingPoints = 14;
        public string OutputDirectory;
        public string Keyword;
        public bool Background = true;
    }

    public sealed class MainForm : Form
    {
        private static readonly Color Ink = Color.FromArgb(28, 45, 37);
        private static readonly Color Muted = Color.FromArgb(86, 105, 96);
        private static readonly Color Green = Color.FromArgb(17, 115, 82);
        private static readonly Color Pale = Color.FromArgb(235, 246, 239);
        private static readonly Color Amber = Color.FromArgb(148, 86, 18);
        private readonly string dataDir;
        private string outputDir;
        private readonly MusicService service;
        private readonly BindingList<MusicItem> items = new BindingList<MusicItem>();
        private readonly TextBox keyword = new TextBox(), directory = new TextBox();
        private readonly Label notice = new Label(), status = new Label(), selection = new Label();
        private readonly Button search = new Button(), download = new Button(), cancel = new Button(), changeDirectory = new Button(), openDirectory = new Button();
        private readonly DataGridView results = new DataGridView();
        private readonly ComboBox pagePicker = new ComboBox();
        private readonly FlowLayoutPanel paging = new FlowLayoutPanel();
        private readonly MenuStrip menu = new MenuStrip();
        private readonly ContextMenuStrip resultMenu = new ContextMenuStrip();
        private readonly ToolTip tips = new ToolTip();
        private readonly List<Button> buttons = new List<Button>();
        private readonly List<ToolStripMenuItem> sizeItems = new List<ToolStripMenuItem>();
        private readonly StringBuilder history = new StringBuilder();
        private readonly ToolStripMenuItem downloadMenu = new ToolStripMenuItem("下载选中(&D)"), chooseDirectoryMenu = new ToolStripMenuItem("下载目录…"), openDirectoryMenu = new ToolStripMenuItem("打开下载目录");
        private readonly ToolStripMenuItem backgroundMenu = new ToolStripMenuItem("后台运行 Chrome"), browserMenu = new ToolStripMenuItem("在浏览器处理"), selectAllMenu = new ToolStripMenuItem("全选结果"), unselectMenu = new ToolStripMenuItem("取消选择");
        private TableLayoutPanel layout;
        private CancellationTokenSource cancellation;
        private Task activeTask;
        private bool busy, closing, finalClose, closeWaiting, loading, browserUsed, disposedService;
        private bool background = true;
        private int readingPoints = 14;
        private Form historyWindow;
        private TextBox historyText;
        private MusicItem contextItem;

        public MainForm(string dataDirectory, string requestedOutputDirectory)
        {
            dataDir = Path.GetFullPath(dataDirectory);
            Directory.CreateDirectory(dataDir);
            outputDir = String.IsNullOrWhiteSpace(requestedOutputDirectory) ? SongTools.DefaultOutputDirectory() : Path.GetFullPath(requestedOutputDirectory);
            service = new MusicService(dataDir);
            service.Log += delegate(string text) { Ui(delegate { AppendHistory(text); }); };
            service.ItemChanged += delegate(MusicItem item) { Ui(delegate { RefreshItem(item); }); };
            BuildUi();
            LoadSettings(String.IsNullOrWhiteSpace(requestedOutputDirectory));
            ApplyReadingSize(false);
            UpdateActions();
            FormClosing += HandleFormClosing;
            FormClosed += delegate { SaveSettings(); DisposeService(); };
        }

        private void BuildUi()
        {
            Text = "音乐下载 v1.1 · YRT-GPT";
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.Sizable;
            ControlBox = true;
            MinimizeBox = MaximizeBox = true;
            Icon = SystemIcons.Application;
            BackColor = Color.FromArgb(246, 250, 247);
            ForeColor = Ink;
            Panel host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) };
            Controls.Add(host);
            BuildMenus();
            Controls.Add(menu);
            MainMenuStrip = menu;
            layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Margin = Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            host.Controls.Add(layout);

            TableLayoutPanel searchRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = Padding.Empty };
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Label searchLabel = MakeLabel("搜索", true);
            searchLabel.Margin = new Padding(0, 10, 12, 0);
            searchRow.Controls.Add(searchLabel, 0, 0);
            keyword.Name = "SearchKeyword";
            keyword.AccessibleName = "歌手或歌名";
            keyword.Dock = DockStyle.Fill;
            keyword.Margin = new Padding(0, 6, 12, 5);
            keyword.BorderStyle = BorderStyle.FixedSingle;
            keyword.MaxLength = 100;
            keyword.KeyDown += async delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                if (!busy) await SearchKeywordAsync();
            };
            searchRow.Controls.Add(keyword, 1, 0);
            ConfigureButton(search, "搜索", true, async delegate { await SearchKeywordAsync(); });
            search.Margin = new Padding(0, 0, 0, 0);
            searchRow.Controls.Add(search, 2, 0);
            layout.Controls.Add(searchRow, 0, 0);

            SetWrap(notice);
            notice.Name = "SearchNotice";
            notice.ForeColor = Muted;
            notice.Text = "输入歌手或歌名片段，搜索后选择要下载的歌曲。";
            notice.Margin = new Padding(0, 9, 0, 10);
            layout.Controls.Add(notice, 0, 1);
            ConfigureResults();
            layout.Controls.Add(results, 0, 2);

            paging.Dock = DockStyle.Top;
            paging.AutoSize = true;
            paging.WrapContents = false;
            paging.Margin = new Padding(0, 7, 0, 0);
            paging.Visible = false;
            Label pageLabel = MakeLabel("更多结果", false);
            pageLabel.Margin = new Padding(0, 5, 10, 0);
            paging.Controls.Add(pageLabel);
            pagePicker.Name = "ResultPages";
            pagePicker.DropDownStyle = ComboBoxStyle.DropDownList;
            pagePicker.DisplayMember = "Label";
            pagePicker.ValueMember = "Url";
            pagePicker.Margin = Padding.Empty;
            pagePicker.SelectedIndexChanged += async delegate
            {
                if (loading || busy || pagePicker.SelectedItem == null) return;
                SearchPage page = pagePicker.SelectedItem as SearchPage;
                if (page == null || String.IsNullOrWhiteSpace(page.Url)) return;
                await RunOperationAsync("正在加载搜索结果…", async delegate(CancellationToken token)
                { SearchResult loaded = await service.LoadPageAsync(page.Url, token); token.ThrowIfCancellationRequested(); ApplySearchResult(loaded); });
            };
            paging.Controls.Add(pagePicker);
            layout.Controls.Add(paging, 0, 3);

            TableLayoutPanel directoryRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 12, 0, 0) };
            directoryRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            directoryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            directoryRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            directoryRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Label outputLabel = MakeLabel("下载到", false);
            outputLabel.Margin = new Padding(0, 10, 12, 0);
            directoryRow.Controls.Add(outputLabel, 0, 0);
            directory.Name = "DownloadDirectory";
            directory.AccessibleName = "下载目录";
            directory.Dock = DockStyle.Fill;
            directory.BorderStyle = BorderStyle.FixedSingle;
            directory.Margin = new Padding(0, 6, 10, 5);
            directory.Leave += delegate { if (!loading && !busy) CommitDirectory(false); };
            directory.KeyDown += delegate(object sender, KeyEventArgs e)
            { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CommitDirectory(true); } };
            directoryRow.Controls.Add(directory, 1, 0);
            ConfigureButton(changeDirectory, "更改…", false, delegate { ChooseDirectory(); });
            changeDirectory.Margin = new Padding(0, 0, 8, 0);
            directoryRow.Controls.Add(changeDirectory, 2, 0);
            ConfigureButton(openDirectory, "打开", false, delegate { OpenOutputDirectory(); });
            openDirectory.Margin = Padding.Empty;
            directoryRow.Controls.Add(openDirectory, 3, 0);
            layout.Controls.Add(directoryRow, 0, 4);

            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = new Padding(0, 12, 0, 0), Padding = Padding.Empty };
            ConfigureButton(download, "下载选中", true, async delegate { await DownloadSelectedAsync(); });
            ConfigureButton(cancel, "取消", false, delegate { CancelOperation(); });
            cancel.Visible = false;
            SetWrap(selection);
            selection.Name = "ResultSelectionCount";
            selection.ForeColor = Muted;
            selection.Margin = new Padding(4, 12, 0, 6);
            actions.Controls.AddRange(new Control[] { download, cancel, selection });
            layout.Controls.Add(actions, 0, 5);

            SetWrap(status);
            status.Name = "OperationStatus";
            status.Text = "准备就绪";
            status.ForeColor = Green;
            status.Margin = new Padding(0, 8, 0, 0);
            layout.Controls.Add(status, 0, 6);
            layout.SizeChanged += delegate { UpdateWrapWidths(); };
        }

        private void BuildMenus()
        {
            menu.Name = "ApplicationMenu";
            menu.Dock = DockStyle.Top;
            menu.RenderMode = ToolStripRenderMode.System;
            menu.BackColor = SystemColors.Control;
            menu.ForeColor = SystemColors.ControlText;
            ToolStripMenuItem file = new ToolStripMenuItem("文件(&F)"), edit = new ToolStripMenuItem("编辑(&E)"), view = new ToolStripMenuItem("视图(&V)"), help = new ToolStripMenuItem("帮助(&H)");
            downloadMenu.ShortcutKeys = Keys.Control | Keys.D;
            downloadMenu.Click += async delegate { await DownloadSelectedAsync(); };
            chooseDirectoryMenu.ShortcutKeys = Keys.Control | Keys.Shift | Keys.D;
            chooseDirectoryMenu.Click += delegate { ChooseDirectory(); };
            openDirectoryMenu.ShortcutKeys = Keys.Control | Keys.O;
            openDirectoryMenu.Click += delegate { OpenOutputDirectory(); };
            ToolStripMenuItem exit = new ToolStripMenuItem("退出(&X)");
            exit.Click += delegate { Close(); };
            file.DropDownItems.AddRange(new ToolStripItem[] { downloadMenu, new ToolStripSeparator(), chooseDirectoryMenu, openDirectoryMenu, new ToolStripSeparator(), exit });

            ToolStripMenuItem cut = new ToolStripMenuItem("剪切搜索内容"), copy = new ToolStripMenuItem("复制搜索内容"), paste = new ToolStripMenuItem("粘贴到搜索框");
            cut.ShortcutKeys = Keys.Control | Keys.X;
            copy.ShortcutKeys = Keys.Control | Keys.C;
            paste.ShortcutKeys = Keys.Control | Keys.V;
            cut.Click += delegate { if (!keyword.ReadOnly) keyword.Cut(); keyword.Focus(); };
            copy.Click += delegate { keyword.Copy(); keyword.Focus(); };
            paste.Click += delegate { if (!keyword.ReadOnly) keyword.Paste(); keyword.Focus(); };
            selectAllMenu.ShortcutKeys = Keys.Control | Keys.A;
            selectAllMenu.Click += delegate { SetAllSelected(true); };
            unselectMenu.Click += delegate { SetAllSelected(false); };
            edit.DropDownItems.AddRange(new ToolStripItem[] { cut, copy, paste, new ToolStripSeparator(), selectAllMenu, unselectMenu });

            ToolStripMenuItem sizes = new ToolStripMenuItem("字号");
            foreach (int size in new[] { 12, 14, 16 })
            {
                int chosen = size;
                ToolStripMenuItem item = new ToolStripMenuItem(size + " 磅") { Tag = size, CheckOnClick = false };
                item.Click += delegate { readingPoints = chosen; ApplyReadingSize(true); SaveSettings(); };
                sizeItems.Add(item);
                sizes.DropDownItems.Add(item);
            }
            ToolStripMenuItem records = new ToolStripMenuItem("下载记录");
            records.Click += delegate { ShowHistory(); };
            backgroundMenu.Checked = true;
            backgroundMenu.CheckOnClick = false;
            backgroundMenu.Click += async delegate
            {
                if (busy || closing) return;
                background = !background;
                backgroundMenu.Checked = background;
                SaveSettings();
                if (browserUsed) await RunOperationAsync(background ? "正在切换到后台…" : "正在显示 Chrome…", async delegate(CancellationToken token)
                { await service.SetBrowserVisibleAsync(!background, token); SetStatus(background ? "Chrome 已在后台运行" : "Chrome 已显示", false); });
            };
            view.DropDownItems.AddRange(new ToolStripItem[] { sizes, records, new ToolStripSeparator(), backgroundMenu });

            ToolStripMenuItem instructions = new ToolStripMenuItem("使用说明");
            instructions.Click += delegate
            {
                ShowTextWindow("使用说明", "搜索音乐\r\n\r\n输入歌手、歌名或歌名片段，然后点击“搜索”或按 Enter。搜索不会自动下载。\r\n\r\n找到歌曲后，可点击那一行的“下载”；也可勾选多首，再点击“下载选中”。\r\n\r\n下载目录可以直接填写完整路径，或点击“更改…”选择。默认保存到桌面的“音乐”文件夹。\r\n\r\nChrome 默认在后台运行。若网站要求验证，使用“帮助 → 在浏览器处理”，完成后重新搜索。\r\n\r\n右键歌曲可以下载或定位已下载文件。“视图”菜单可调整字号、查看下载记录。\r\n\r\n只下载你有权下载的公开试听内容。", false);
            };
            browserMenu.Click += async delegate
            {
                if (busy || closing) return;
                await RunOperationAsync("正在显示 Chrome…", async delegate(CancellationToken token)
                {
                    browserUsed = true;
                    await service.SetBrowserVisibleAsync(true, token);
                    background = false;
                    backgroundMenu.Checked = false;
                    SaveSettings();
                    SetStatus("请在 Chrome 中处理网页提示，完成后重新搜索。", false);
                });
            };
            ToolStripMenuItem about = new ToolStripMenuItem("关于");
            about.Click += delegate { ShowTextWindow("关于", AboutInformation(), true); };
            help.DropDownItems.AddRange(new ToolStripItem[] { instructions, browserMenu, new ToolStripSeparator(), about });
            menu.Items.AddRange(new ToolStripItem[] { file, edit, view, help });
        }

        private void ConfigureResults()
        {
            results.Name = "SearchResults";
            results.Dock = DockStyle.Fill;
            results.Margin = Padding.Empty;
            results.AutoGenerateColumns = false;
            results.AllowUserToAddRows = results.AllowUserToDeleteRows = results.AllowUserToResizeRows = false;
            results.RowHeadersVisible = false;
            results.MultiSelect = false;
            results.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            results.BackgroundColor = Color.White;
            results.BorderStyle = BorderStyle.FixedSingle;
            results.GridColor = Color.FromArgb(219, 231, 224);
            results.EnableHeadersVisualStyles = false;
            results.ColumnHeadersDefaultCellStyle.BackColor = Pale;
            results.ColumnHeadersDefaultCellStyle.ForeColor = Ink;
            results.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
            results.DefaultCellStyle.Padding = new Padding(8, 6, 8, 6);
            results.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            results.DefaultCellStyle.SelectionBackColor = Color.FromArgb(211, 235, 222);
            results.DefaultCellStyle.SelectionForeColor = Ink;
            results.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 251, 249);
            results.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            results.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            results.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", DataPropertyName = "Selected", HeaderText = "选择", AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 100, MinimumWidth = 90, FlatStyle = FlatStyle.Standard });
            AddTextColumn("Title", "歌名", 47, 220);
            AddTextColumn("Artist", "歌手", 32, 190);
            AddTextColumn("State", "状态", 21, 160);
            results.Columns.Add(new DataGridViewButtonColumn { Name = "Download", HeaderText = "操作", Text = "下载", UseColumnTextForButtonValue = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 130, MinimumWidth = 110, FlatStyle = FlatStyle.Flat });
            results.DataSource = items;
            results.CurrentCellDirtyStateChanged += delegate { if (results.IsCurrentCellDirty) results.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            results.CellValueChanged += delegate { UpdateSelection(); };
            results.DataError += delegate(object sender, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; };
            results.CellContentClick += async delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex < 0 || e.RowIndex >= items.Count || busy) return;
                if (results.Columns[e.ColumnIndex].Name == "Download") await DownloadItemsAsync(new List<MusicItem> { items[e.RowIndex] });
            };
            results.CellToolTipTextNeeded += delegate(object sender, DataGridViewCellToolTipTextNeededEventArgs e)
            { if (e.RowIndex >= 0 && e.RowIndex < items.Count) e.ToolTipText = items[e.RowIndex].Detail ?? ""; };
            ToolStripMenuItem downloadThis = new ToolStripMenuItem("下载此首"), locate = new ToolStripMenuItem("定位已下载文件");
            downloadThis.Click += async delegate { if (contextItem != null) await DownloadItemsAsync(new List<MusicItem> { contextItem }); };
            locate.Click += delegate { if (contextItem != null) LocateFile(contextItem.FilePath); };
            resultMenu.Items.AddRange(new ToolStripItem[] { downloadThis, locate });
            resultMenu.Opening += delegate(object sender, CancelEventArgs e)
            {
                Point point = results.PointToClient(Cursor.Position);
                DataGridView.HitTestInfo hit = results.HitTest(point.X, point.Y);
                if (hit.RowIndex < 0 || hit.RowIndex >= items.Count) { contextItem = null; e.Cancel = true; return; }
                contextItem = items[hit.RowIndex];
                results.CurrentCell = results.Rows[hit.RowIndex].Cells["Title"];
                downloadThis.Enabled = !busy && !closing;
                locate.Enabled = !String.IsNullOrWhiteSpace(contextItem.FilePath) && File.Exists(contextItem.FilePath);
            };
            results.ContextMenuStrip = resultMenu;
        }

        private void AddTextColumn(string property, string title, int weight, int minimum)
        {
            results.Columns.Add(new DataGridViewTextBoxColumn { Name = property, DataPropertyName = property, HeaderText = title, FillWeight = weight, MinimumWidth = minimum, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        }

        private async Task SearchKeywordAsync()
        {
            if (busy || closing) return;
            string query = keyword.Text.Trim();
            if (query.Length == 0) { SetStatus("请输入歌手、歌名或歌名片段。", false); keyword.Focus(); return; }
            await RunOperationAsync("正在搜索“" + query + "”…", async delegate(CancellationToken token)
            {
                browserUsed = true;
                if (service.IsBackground != background) await service.SetBrowserVisibleAsync(!background, token);
                SearchResult found = await service.SearchAsync(query, token);
                token.ThrowIfCancellationRequested();
                ApplySearchResult(found);
            });
        }

        private void ApplySearchResult(SearchResult result)
        {
            if (result == null) throw new InvalidOperationException("网站没有返回可读取的搜索结果，请稍后重试。");
            items.RaiseListChangedEvents = false;
            items.Clear();
            if (result.Items != null) foreach (MusicItem item in result.Items) { item.Selected = false; items.Add(item); }
            items.RaiseListChangedEvents = true;
            items.ResetBindings();
            loading = true;
            pagePicker.Items.Clear();
            bool hasPages = result.Pages != null && result.Pages.Count > 0;
            if (hasPages)
            {
                pagePicker.Items.Add(new SearchPage { Label = "选择结果页…", Url = "" });
                foreach (SearchPage page in result.Pages) pagePicker.Items.Add(page);
                pagePicker.SelectedIndex = 0;
            }
            paging.Visible = hasPages;
            loading = false;
            notice.Text = String.IsNullOrWhiteSpace(result.Notice) ? "搜索结果可逐首下载，也可勾选多首后下载。" : result.Notice;
            SetStatus(items.Count == 0 ? "未找到相关歌曲，可换用歌手或较短的歌名片段。" : "找到 " + items.Count + " 首歌曲，请选择要下载的版本。", false);
            UpdateSelection();
            SaveSettings();
        }

        private async Task DownloadSelectedAsync()
        {
            if (busy || closing) return;
            results.EndEdit();
            List<MusicItem> chosen = items.Where(x => x.Selected).ToList();
            if (chosen.Count == 0) { SetStatus("请先勾选歌曲，或点击某一行的“下载”。", false); return; }
            await DownloadItemsAsync(chosen);
        }

        private async Task DownloadItemsAsync(IList<MusicItem> chosen)
        {
            if (busy || closing || chosen == null || chosen.Count == 0 || !CommitDirectory(true)) return;
            string destination = outputDir;
            await RunOperationAsync("正在下载 " + chosen.Count + " 首歌曲…", async delegate(CancellationToken token)
            {
                browserUsed = true;
                if (service.IsBackground != background) await service.SetBrowserVisibleAsync(!background, token);
                await service.DownloadAsync(chosen, destination, token);
                token.ThrowIfCancellationRequested();
                int complete = chosen.Count(x => x.State == "已完成" || (!String.IsNullOrWhiteSpace(x.FilePath) && File.Exists(x.FilePath)));
                SetStatus("本次下载结束：已保存 " + complete + " / " + chosen.Count + " 首。详细结果见歌曲状态。", false);
            });
        }

        private async Task RunOperationAsync(string initialStatus, Func<CancellationToken, Task> operation)
        {
            if (busy || closing) return;
            busy = true;
            cancellation = new CancellationTokenSource();
            SetStatus(initialStatus, false);
            UpdateActions();
            SaveSettings();
            try
            {
                activeTask = operation(cancellation.Token);
                await activeTask;
            }
            catch (OperationCanceledException) { if (!closing) SetStatus("已取消当前操作，已经下载的文件保留。", false); }
            catch (Exception ex)
            {
                if (!closing) SetStatus("未完成：" + ex.Message, true);
                AppendHistory("未完成：" + ex.Message);
            }
            finally
            {
                activeTask = null;
                if (cancellation != null) { cancellation.Dispose(); cancellation = null; }
                busy = false;
                UpdateActions();
                SaveSettings();
            }
        }

        private void CancelOperation()
        {
            if (cancellation == null || cancellation.IsCancellationRequested) return;
            cancellation.Cancel();
            SetStatus("正在取消，请稍候…", false);
            UpdateActions();
        }

        private void RefreshItem(MusicItem item)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (Object.ReferenceEquals(items[i], item) || (!String.IsNullOrEmpty(item.SourcePage) && items[i].SourcePage == item.SourcePage))
                { if (!Object.ReferenceEquals(items[i], item)) items[i] = item; items.ResetItem(i); break; }
            }
            UpdateSelection();
            if (busy && !closing && cancellation != null && !cancellation.IsCancellationRequested)
                SetStatus(item.Title + " · " + item.State, item.State == "失败");
        }

        private void SetAllSelected(bool selected)
        {
            if (busy || closing) return;
            foreach (MusicItem item in items) item.Selected = selected;
            items.ResetBindings();
            UpdateSelection();
        }

        private void UpdateSelection()
        {
            selection.Text = "共 " + items.Count + " 首 · 已选 " + items.Count(x => x.Selected) + " 首";
            UpdateActions();
        }

        private void UpdateActions()
        {
            bool idle = !busy && !closing;
            search.Enabled = idle;
            search.BackColor = search.Enabled ? Green : Pale;
            keyword.ReadOnly = !idle;
            directory.ReadOnly = !idle;
            changeDirectory.Enabled = chooseDirectoryMenu.Enabled = idle;
            download.Enabled = downloadMenu.Enabled = idle && items.Any(x => x.Selected);
            download.BackColor = download.Enabled ? Green : Pale;
            cancel.Visible = busy;
            cancel.Enabled = busy && !closing && cancellation != null && !cancellation.IsCancellationRequested;
            browserMenu.Enabled = backgroundMenu.Enabled = idle;
            pagePicker.Enabled = idle;
            selectAllMenu.Enabled = unselectMenu.Enabled = idle && items.Count > 0;
            results.Columns["Selected"].ReadOnly = !idle;
        }

        private bool CommitDirectory(bool showError)
        {
            try
            {
                string candidate = Environment.ExpandEnvironmentVariables(directory.Text.Trim().Trim('"'));
                if (String.IsNullOrWhiteSpace(candidate) || !Path.IsPathRooted(candidate)) throw new InvalidOperationException("请输入完整的下载目录路径，或点击“更改…”选择文件夹。");
                outputDir = Path.GetFullPath(candidate);
                directory.Text = outputDir;
                tips.SetToolTip(directory, outputDir);
                SaveSettings();
                return true;
            }
            catch (Exception ex)
            {
                if (showError) { SetStatus("下载目录未确认：" + ex.Message, true); directory.Focus(); }
                return false;
            }
        }

        private void ChooseDirectory()
        {
            if (busy || closing) return;
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择音乐下载目录";
                dialog.ShowNewFolderButton = true;
                dialog.SelectedPath = Directory.Exists(outputDir) ? outputDir : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                directory.Text = dialog.SelectedPath;
                CommitDirectory(true);
            }
        }

        private void OpenOutputDirectory()
        {
            if (!CommitDirectory(true)) return;
            try { Directory.CreateDirectory(outputDir); Process.Start(new ProcessStartInfo(outputDir) { UseShellExecute = true }); }
            catch (Exception ex) { SetStatus("无法打开目录：" + ex.Message, true); }
        }

        private void LocateFile(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { SetStatus("无法定位文件：" + ex.Message, true); }
        }

        private void SetStatus(string text, bool error)
        {
            status.Text = text.Length > 260 ? text.Substring(0, 257) + "…" : text;
            status.ForeColor = error ? Amber : Green;
            tips.SetToolTip(status, text);
        }

        private void AppendHistory(string text)
        {
            string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + text + Environment.NewLine;
            history.Append(line);
            if (history.Length > 200000) history.Remove(0, history.Length - 150000);
            if (historyText != null && !historyText.IsDisposed) historyText.AppendText(line);
            try { File.AppendAllText(Path.Combine(dataDir, "界面记录.txt"), line, Encoding.UTF8); } catch { }
        }

        private void ShowHistory()
        {
            if (historyWindow != null && !historyWindow.IsDisposed) { historyWindow.Activate(); return; }
            historyWindow = CreateTextWindow("下载记录", history.ToString(), false);
            historyText = (TextBox)historyWindow.Controls[0];
            historyWindow.FormClosed += delegate { historyWindow = null; historyText = null; };
            historyWindow.Show(this);
        }

        private string AboutInformation()
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("YRT Music v1.1");
            text.AppendLine("编辑者：YRT-GPT");
            text.AppendLine("正式发布日期：" + ReleaseInfo.DisplayDate);
            text.AppendLine();
            text.AppendLine("输入歌手或部分歌名，搜索后自行选择歌曲下载。支持逐首下载、勾选多首，以及自选下载目录。");
            text.AppendLine("Windows 10 / 11 64 位；需要 .NET Framework 4.8 和 Google Chrome。");
            text.AppendLine();
            text.AppendLine("版本历史");
            text.AppendLine(ReleaseInfo.History);
            text.AppendLine();
            text.AppendLine("软件采用 MIT 许可证。请仅下载你有权保存的公开试听内容。");
            text.AppendLine("项目：https://github.com/yrt-coder/YRTMusic");
            return text.ToString();
        }

        private void ShowTextWindow(string title, string text, bool signature)
        {
            using (Form dialog = CreateTextWindow(title, text, signature)) dialog.ShowDialog(this);
        }

        private Form CreateTextWindow(string title, string text, bool signature)
        {
            Form dialog = new Form { Text = title + " · YRT-GPT", StartPosition = FormStartPosition.CenterParent, Font = new Font("Microsoft YaHei UI", readingPoints, GraphicsUnit.Point), AutoScaleMode = AutoScaleMode.None, BackColor = Color.White, MinimizeBox = true, MaximizeBox = true, ShowInTaskbar = false, Padding = new Padding(16) };
            Rectangle area = Screen.FromControl(this).WorkingArea;
            dialog.Size = new Size(Math.Min(area.Width - 80, Font.Height * 39), Math.Min(area.Height - 80, Font.Height * 25));
            dialog.MinimumSize = new Size(Math.Min(760, area.Width - 40), Math.Min(520, area.Height - 40));
            TextBox textBox = new TextBox { Name = signature ? "SignatureInformation" : "InformationText", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Color.White, Text = text };
            dialog.Controls.Add(textBox);
            return dialog;
        }

        private void LoadSettings(bool useSavedOutput)
        {
            loading = true;
            try
            {
                string file = Path.Combine(dataDir, "设置.json");
                if (File.Exists(file))
                {
                    SavedSettings saved = new JavaScriptSerializer().Deserialize<SavedSettings>(File.ReadAllText(file, Encoding.UTF8));
                    if (saved != null)
                    {
                        if (saved.ReadingPoints == 12 || saved.ReadingPoints == 14 || saved.ReadingPoints == 16) readingPoints = saved.ReadingPoints;
                        keyword.Text = saved.Keyword ?? "";
                        background = saved.Background;
                        if (useSavedOutput && !String.IsNullOrWhiteSpace(saved.OutputDirectory) && Path.IsPathRooted(saved.OutputDirectory)) outputDir = Path.GetFullPath(saved.OutputDirectory);
                    }
                }
                string record = Path.Combine(dataDir, "界面记录.txt");
                if (File.Exists(record)) { string prior = File.ReadAllText(record, Encoding.UTF8); history.Append(prior.Length > 150000 ? prior.Substring(prior.Length - 150000) : prior); }
            }
            catch (Exception ex) { AppendHistory("读取上次设置未完成：" + ex.Message); }
            directory.Text = outputDir;
            tips.SetToolTip(directory, outputDir);
            backgroundMenu.Checked = background;
            loading = false;
        }

        private void SaveSettings()
        {
            if (loading) return;
            try
            {
                SavedSettings saved = new SavedSettings { ReadingPoints = readingPoints, OutputDirectory = outputDir, Keyword = keyword.Text, Background = background };
                File.WriteAllText(Path.Combine(dataDir, "设置.json"), new JavaScriptSerializer().Serialize(saved), Encoding.UTF8);
            }
            catch { }
        }

        private async void HandleFormClosing(object sender, FormClosingEventArgs e)
        {
            if (finalClose || !busy) return;
            e.Cancel = true;
            if (closeWaiting) return;
            closeWaiting = true;
            closing = true;
            SaveSettings();
            SetStatus("正在取消当前操作并退出，请稍候…", false);
            UpdateActions();
            Task wait = activeTask;
            if (cancellation != null) cancellation.Cancel();
            try { if (wait != null) await wait; } catch { }
            finalClose = true;
            if (!IsDisposed) Close();
        }

        private void Ui(Action action)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            if (!InvokeRequired) { action(); return; }
            try { BeginInvoke(action); } catch (InvalidOperationException) { }
        }

        private void ApplyReadingSize(bool keepLocation)
        {
            Point previous = Location;
            SuspendLayout();
            try
            {
                Font = new Font("Microsoft YaHei UI", readingPoints, FontStyle.Regular, GraphicsUnit.Point);
                menu.Font = new Font("Microsoft YaHei UI", readingPoints, FontStyle.Regular, GraphicsUnit.Point);
                ApplyMenuFont(menu.Items, menu.Font);
                resultMenu.Font = new Font("Microsoft YaHei UI", readingPoints, FontStyle.Regular, GraphicsUnit.Point);
                foreach (ToolStripMenuItem item in sizeItems) item.Checked = (int)item.Tag == readingPoints;
                foreach (Button button in buttons)
                {
                    button.Font = new Font("Microsoft YaHei UI", readingPoints, button == search || button == download ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
                    button.MinimumSize = button.MaximumSize = Size.Empty;
                    Size text = TextRenderer.MeasureText(button.Text, button.Font);
                    button.Size = new Size(text.Width + 36, text.Height + 20);
                    button.MinimumSize = button.MaximumSize = button.Size;
                }
                results.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
                results.ColumnHeadersHeight = Font.Height + 20;
                foreach (DataGridViewColumn column in results.Columns)
                {
                    int measured = TextRenderer.MeasureText(column.HeaderText, Font).Width + 30;
                    if (column.Name == "Selected" || column.Name == "Download") { column.MinimumWidth = measured + 15; column.Width = measured + 15; }
                }
                results.RowTemplate.Height = Font.Height + 22;
                results.AutoResizeRows(DataGridViewAutoSizeRowsMode.AllCells);
                pagePicker.Width = Math.Max(340, Font.Height * 12);
                Rectangle area = Screen.FromControl(this).WorkingArea;
                Size frame = SizeFromClientSize(Size.Empty);
                MinimumSize = Size.Empty;
                ClientSize = new Size(Math.Min(area.Width - frame.Width - 24, Math.Max(1080, Font.Height * 43)), Math.Min(area.Height - frame.Height - 24, Math.Max(740, Font.Height * 26)));
                MinimumSize = new Size(Math.Min(area.Width - 24, Math.Max(960, Font.Height * 28)), Math.Min(area.Height - 24, Math.Max(640, Font.Height * 17)));
                if (keepLocation) Location = new Point(Math.Max(area.Left, Math.Min(previous.X, area.Right - Width)), Math.Max(area.Top, Math.Min(previous.Y, area.Bottom - Height)));
                if (historyWindow != null && !historyWindow.IsDisposed) historyWindow.Font = new Font("Microsoft YaHei UI", readingPoints, GraphicsUnit.Point);
                UpdateWrapWidths();
            }
            finally { ResumeLayout(true); }
        }

        private static void ApplyMenuFont(ToolStripItemCollection items, Font font)
        {
            foreach (ToolStripItem item in items)
            {
                item.Font = font;
                ToolStripDropDownItem nested = item as ToolStripDropDownItem;
                if (nested != null) ApplyMenuFont(nested.DropDownItems, font);
            }
        }

        private void UpdateWrapWidths()
        {
            if (layout == null) return;
            int width = Math.Max(240, layout.ClientSize.Width - 6);
            notice.MaximumSize = status.MaximumSize = new Size(width, 0);
        }

        private static Label MakeLabel(string text, bool bold)
        {
            Label label = new Label { Text = text, AutoSize = true, UseMnemonic = false, Margin = Padding.Empty };
            return label;
        }

        private static void SetWrap(Label label)
        {
            label.AutoSize = true;
            label.Dock = DockStyle.Top;
            label.UseMnemonic = false;
        }

        private void ConfigureButton(Button button, string text, bool primary, EventHandler handler)
        {
            button.Text = text;
            button.UseMnemonic = false;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(179, 205, 189);
            button.BackColor = primary ? Green : Color.White;
            button.ForeColor = primary ? Color.White : Green;
            button.Margin = new Padding(0, 0, 10, 0);
            button.Cursor = Cursors.Hand;
            button.Click += handler;
            buttons.Add(button);
        }

        private void DisposeService()
        {
            if (disposedService) return;
            disposedService = true;
            service.Dispose();
            if (historyWindow != null && !historyWindow.IsDisposed) historyWindow.Close();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { DisposeService(); tips.Dispose(); resultMenu.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
