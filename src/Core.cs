using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace YrtMusic
{
    public sealed class MusicItem
    {
        public bool Selected { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public string SourcePage { get; set; }
        public string State { get; set; }
        public string Detail { get; set; }
        public string FilePath { get; set; }
    }
    public sealed class SearchPage
    {
        public string Label { get; set; }
        public string Url { get; set; }
    }
    public sealed class SearchResult
    {
        public List<MusicItem> Items { get; set; }
        public List<SearchPage> Pages { get; set; }
        public string QueryUsed { get; set; }
        public string Notice { get; set; }
    }
    public sealed class MusicService : IDisposable
    {
        private const string Home = "https://www.gequhai.com/";
        private readonly string dataDir;
        private readonly SemaphoreSlim operation = new SemaphoreSlim(1, 1);
        private readonly object logLock = new object();
        private ChromeSession browser;
        private bool background = true, disposed;
        public bool IsBackground { get { return background; } }
        public event Action<string> Log;
        public event Action<MusicItem> ItemChanged;

        public MusicService(string directory)
        {
            dataDir = Path.GetFullPath(directory);
            Directory.CreateDirectory(dataDir);
        }
        private async Task EnsureBrowserAsync(CancellationToken token)
        {
            if (disposed) throw new ObjectDisposedException("MusicService");
            if (browser != null && browser.IsRunning) return;
            if (browser != null) browser.Dispose();
            browser = new ChromeSession();
            await browser.StartAsync(Path.Combine(dataDir, "chrome-profile"), background, token).ConfigureAwait(false);
            WriteLog(background ? "后台浏览器已就绪。" : "验证浏览器已打开。");
        }
        public async Task SetBrowserVisibleAsync(bool visible, CancellationToken token)
        {
            await operation.WaitAsync(token).ConfigureAwait(false);
            try
            {
                bool requestedBackground = !visible;
                string current = browser == null ? Home : browser.PageUrl;
                if (!IsSiteUrl(current)) current = Home;
                if (background != requestedBackground)
                {
                    if (browser != null) { browser.Dispose(); browser = null; }
                    background = requestedBackground;
                }
                await EnsureBrowserAsync(token).ConfigureAwait(false);
                await browser.NavigateAsync(current, token).ConfigureAwait(false);
                if (visible) await browser.BringToFrontAsync(token).ConfigureAwait(false);
            }
            finally { operation.Release(); }
        }
        public async Task<SearchResult> SearchAsync(string keyword, CancellationToken token)
        {
            string query = CleanQuery(keyword);
            if (query.Length == 0) throw new ArgumentException("请输入歌名、歌手或部分关键词。");
            if (query.Length > 50) throw new ArgumentException("网站搜索最多支持 50 个字，请缩短关键词。");
            await operation.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await EnsureBrowserAsync(token).ConfigureAwait(false);
                WriteLog("搜索：" + query);
                await browser.NavigateAsync(Home, token).ConfigureAwait(false);
                await WaitPageAsync(s => Text(s, "path") == "/" && Flag(s, "hasInput"), 60, token).ConfigureAwait(false);
                string script = "(()=>{const i=document.querySelector('#s-input,#s-input-line'),b=document.querySelector('#s-btn,#s-btn-line');if(!i||!b)return false;i.value=" + new JavaScriptSerializer().Serialize(query) + ";i.dispatchEvent(new Event('input',{bubbles:true}));i.dispatchEvent(new Event('change',{bubbles:true}));b.click();return true;})()";
                object submitted = await browser.EvaluateAsync(script, token).ConfigureAwait(false);
                if (!(submitted is bool) || !(bool)submitted) throw new InvalidOperationException("网页的搜索入口发生变化，请稍后重试。");
                // The normal site sometimes resolves an uncached query for about a minute.
                Dictionary<string, object> state = await WaitPageAsync(s => Text(s, "path").TrimEnd('/') == "/search" && String.Equals(CleanQuery(Text(s, "searchKey")), query, StringComparison.OrdinalIgnoreCase), 100, token).ConfigureAwait(false);
                return ReadResults(state, query);
            }
            finally { operation.Release(); }
        }
        public async Task<SearchResult> LoadPageAsync(string url, CancellationToken token)
        {
            Uri target;
            if (!TrySiteUri(url, out target) || target.AbsolutePath.TrimEnd('/') != "/search") throw new ArgumentException("该地址不是网站提供的搜索结果页。");
            await operation.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await EnsureBrowserAsync(token).ConfigureAwait(false);
                await browser.NavigateAsync(url, token).ConfigureAwait(false);
                var state = await WaitPageAsync(s => SamePage(Text(s, "url"), url), 100, token).ConfigureAwait(false);
                return ReadResults(state, Text(state, "searchKey"));
            }
            finally { operation.Release(); }
        }
        public async Task DownloadAsync(IList<MusicItem> items, string outputDirectory, CancellationToken token)
        {
            if (items == null || items.Count == 0) throw new ArgumentException("请选择需要下载的歌曲。");
            if (String.IsNullOrWhiteSpace(outputDirectory)) throw new ArgumentException("请先选择下载目录。");
            string output = Path.GetFullPath(outputDirectory);
            MusicItem[] selected = items.ToArray();
            await operation.WaitAsync(token).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(output);
                await EnsureBrowserAsync(token).ConfigureAwait(false);
                foreach (MusicItem item in selected)
                {
                    token.ThrowIfCancellationRequested();
                    string manual = null;
                    try { await DownloadOneAsync(item, output, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { Change(item, "已取消", "本次下载已取消。"); throw; }
                    catch (SiteAttentionException ex) { Change(item, "需处理", ex.Message); manual = ex.Message; }
                    catch (Exception ex) { Change(item, "失败", ex.Message); WriteLog(item.Title + "：" + ex.Message); }
                    if (manual != null) throw new InvalidOperationException(manual);
                    await Task.Delay(1200, token).ConfigureAwait(false);
                }
            }
            finally { operation.Release(); }
        }
        private async Task DownloadOneAsync(MusicItem item, string output, CancellationToken token)
        {
            Uri source;
            if (item == null || !TrySiteUri(item.SourcePage, out source) || !System.Text.RegularExpressions.Regex.IsMatch(source.AbsolutePath, @"^/play/\d+/?$"))
                throw new InvalidDataException("这首歌没有有效的来源页面，请重新搜索。");
            string existingReason;
            if (!String.IsNullOrWhiteSpace(item.FilePath) && File.Exists(item.FilePath) && item.State == "已完成" && String.Equals(Path.GetDirectoryName(Path.GetFullPath(item.FilePath)).TrimEnd(Path.DirectorySeparatorChar), output.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) && SongTools.IsMp3(item.FilePath, out existingReason))
            {
                Change(item, "已完成", "文件已在当前目录中，可双击打开位置。"); return;
            }
            Change(item, "检查音源", "正在检查所选歌曲的试听 MP3。");
            await browser.NavigateAsync(item.SourcePage, token).ConfigureAwait(false);
            await WaitPageAsync(s => SamePage(Text(s, "url"), item.SourcePage) && Flag(s, "hasDownload"), 60, token).ConfigureAwait(false);
            await browser.EvaluateAsync("(()=>{const b=document.querySelector('#btn-download-mp3');if(!b)return false;b.click();return true;})()", token).ConfigureAwait(false);
            string media = await WaitQualityAsync(token).ConfigureAwait(false);
            Uri mediaUri;
            if (!Uri.TryCreate(media, UriKind.Absolute, out mediaUri) || (mediaUri.Scheme != "https" && mediaUri.Scheme != "http") || !String.IsNullOrEmpty(mediaUri.UserInfo))
                throw new InvalidDataException("网站没有提供可直接下载的试听链接。");
            Change(item, "下载中", "正在下载试听 MP3。");
            string incoming = Path.Combine(dataDir, "incoming");
            ChromeDownload download = null;
            try
            {
                download = await browser.DownloadAsync(media, incoming, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                string reason;
                if (!SongTools.IsMp3(download.FullPath, out reason)) throw new InvalidDataException("下载内容不是有效 MP3：" + reason);
                token.ThrowIfCancellationRequested();
                item.FilePath = SongTools.MoveToMusic(download.FullPath, output, item.Title, item.Artist);
                Change(item, "已完成", "已保存：" + item.FilePath);
                WriteLog("已保存：" + item.FilePath);
            }
            finally
            {
                if (download != null && File.Exists(download.FullPath))
                {
                    string path = Path.GetFullPath(download.FullPath), prefix = Path.GetFullPath(incoming).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) try { File.Delete(path); } catch (IOException) { }
                }
            }
        }
        private async Task<Dictionary<string, object>> WaitPageAsync(Func<Dictionary<string, object>, bool> ready, int seconds, CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                Dictionary<string, object> page = null;
                try { page = AsDict(await browser.EvaluateAsync(PageSnapshot, token).ConfigureAwait(false)); }
                catch (InvalidOperationException ex)
                {
                    if (!browser.IsRunning) throw new SiteAttentionException("后台浏览器已关闭，请重试搜索或下载。");
                    if (ex.Message.IndexOf("context", StringComparison.OrdinalIgnoreCase) < 0 && ex.Message.IndexOf("navigation", StringComparison.OrdinalIgnoreCase) < 0) throw;
                }
                if (page != null)
                {
                    CheckPage(page);
                    if (Text(page, "ready") == "complete" && ready(page)) return page;
                }
                await Task.Delay(400, token).ConfigureAwait(false);
            }
            throw new SiteAttentionException("网站暂时未响应。可重试，或从“帮助 → 在浏览器处理”检查网站后再搜索。");
        }
        private async Task<string> WaitQualityAsync(CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(35);
            while (DateTime.UtcNow < deadline)
            {
                var state = AsDict(await browser.EvaluateAsync(PageSnapshot, token).ConfigureAwait(false));
                CheckPage(state);
                var quality = AsDict(await browser.EvaluateAsync(QualitySnapshot, token).ConfigureAwait(false));
                if (Flag(quality, "found") && !String.IsNullOrWhiteSpace(Text(quality, "url"))) return Text(quality, "url");
                await Task.Delay(400, token).ConfigureAwait(false);
            }
            throw new InvalidDataException("这首歌暂时没有可用的公开试听 MP3 链接。");
        }
        private SearchResult ReadResults(Dictionary<string, object> state, string query)
        {
            var items = new List<MusicItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object entry in Values(state, "rows"))
            {
                var row = AsDict(entry); string url = Text(row, "url"); Uri uri;
                if (TrySiteUri(url, out uri) && System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, @"^/play/\d+/?$") && seen.Add(uri.AbsoluteUri))
                    items.Add(new MusicItem { Title = Text(row, "title"), Artist = Text(row, "artist"), SourcePage = uri.AbsoluteUri, State = "未下载", Detail = "点击“下载”检查并保存试听 MP3。", FilePath = "" });
            }
            var pages = new List<SearchPage>();
            foreach (object entry in Values(state, "pages"))
            {
                var row = AsDict(entry); Uri uri; string url = Text(row, "url");
                if (TrySiteUri(url, out uri) && uri.AbsolutePath.TrimEnd('/') == "/search" && !pages.Any(p => p.Url == uri.AbsoluteUri)) pages.Add(new SearchPage { Label = Text(row, "label"), Url = uri.AbsoluteUri });
            }
            WriteLog("搜索“" + query + "”：网站本页返回 " + items.Count + " 首，等待选择；未下载任何歌曲。");
            return new SearchResult { Items = items, Pages = pages, QueryUsed = query, Notice = items.Count == 0 ? "没有找到相关歌曲，可换歌手名或更短的关键词。" : "本页 " + items.Count + " 首，选择喜欢的歌曲下载。" };
        }
        private static void CheckPage(Dictionary<string, object> page)
        {
            if (page == null) return;
            string text = Text(page, "text");
            foreach (string marker in new[] { "访问异常", "请求过于频繁", "访问过于频繁", "人机验证", "Forbidden", "Access denied", "Just a moment", "Verify you are human", "安全验证", "429 Too Many" })
                if (text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) throw new SiteAttentionException("网站显示“" + marker + "”。请从“帮助 → 在浏览器处理”正常处理后重试。");
            string url = Text(page, "url");
            if (url == "about:blank" || String.IsNullOrEmpty(url)) return;
            if (!IsSiteUrl(url)) throw new SiteAttentionException("当前没有加载歌曲海正常页面，请检查网络，或从“帮助 → 在浏览器处理”后重试。");
        }
        private void Change(MusicItem item, string state, string detail)
        {
            item.State = state; item.Detail = detail;
            var handler = ItemChanged; if (handler != null) handler(item);
            try
            {
                string line = new JavaScriptSerializer().Serialize(new { time = Now(), title = item.Title, artist = item.Artist, page = item.SourcePage, state = state, detail = detail, file = item.FilePath });
                lock (logLock) File.AppendAllText(Path.Combine(dataDir, "downloads.jsonl"), line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException) { }
        }
        private void WriteLog(string message)
        {
            string line = Now() + "  " + message;
            var handler = Log; if (handler != null) handler(line);
            try { lock (logLock) File.AppendAllText(Path.Combine(dataDir, "run.log"), line + Environment.NewLine, Encoding.UTF8); } catch (IOException) { }
        }
        private static string Now() { return DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss zzz"); }
        private static string CleanQuery(string value) { return System.Text.RegularExpressions.Regex.Replace((value ?? "").Normalize(NormalizationForm.FormKC).Replace("%", "").Trim(), @"\s+", " "); }
        private static bool TrySiteUri(string value, out Uri uri) { return Uri.TryCreate(value, UriKind.Absolute, out uri) && (uri.Scheme == "https" || uri.Scheme == "http") && String.IsNullOrEmpty(uri.UserInfo) && (uri.Host == "www.gequhai.com" || uri.Host == "gequhai.com"); }
        private static bool IsSiteUrl(string value) { Uri uri; return TrySiteUri(value, out uri); }
        private static bool SamePage(string a, string b)
        {
            Uri first, second;
            return TrySiteUri(a, out first) && TrySiteUri(b, out second) && String.Equals(Uri.UnescapeDataString(first.PathAndQuery).Replace('+', ' '), Uri.UnescapeDataString(second.PathAndQuery).Replace('+', ' '), StringComparison.OrdinalIgnoreCase);
        }
        private static Dictionary<string, object> AsDict(object value) { return value as Dictionary<string, object>; }
        private static string Text(Dictionary<string, object> d, string key) { object value; return d != null && d.TryGetValue(key, out value) && value != null ? value.ToString() : ""; }
        private static bool Flag(Dictionary<string, object> d, string key) { object value; return d != null && d.TryGetValue(key, out value) && value is bool && (bool)value; }
        private static IEnumerable Values(Dictionary<string, object> d, string key) { object value; return d != null && d.TryGetValue(key, out value) && value is IEnumerable ? (IEnumerable)value : new object[0]; }
        public void Dispose() { if (disposed) return; disposed = true; if (browser != null) browser.Dispose(); }
        private sealed class SiteAttentionException : Exception { public SiteAttentionException(string message) : base(message) { } }

        private const string PageSnapshot = @"(()=>({url:location.href,path:location.pathname,title:document.title,ready:document.readyState,text:(document.body?document.body.innerText:'').slice(0,14000),hasInput:!!document.querySelector('#s-input,#s-input-line'),hasDownload:!!document.querySelector('#btn-download-mp3'),searchKey:(document.querySelector('#s-input-line')||{}).value||'',rows:[...document.querySelectorAll('table tbody tr')].map(tr=>{const a=tr.querySelector('a[href^=""/play/""]'),td=tr.querySelectorAll('td');return a?{title:a.textContent.trim(),artist:td.length>=3?td[2].textContent.trim():'',url:a.href}:null}).filter(Boolean),pages:[...document.querySelectorAll('.pagination a[href],a.page-link[href],a[rel=""next""][href],a[rel=""prev""][href]')].filter(a=>!a.closest('.disabled')&&a.getAttribute('aria-disabled')!=='true').map(a=>({label:a.textContent.trim()||a.getAttribute('aria-label')||'更多',url:a.href}))}))()";
        private const string QualitySnapshot = @"(()=>{const toggle=document.querySelector('#dl-toggle'),collapse=document.querySelector('#dl-collapse');if(toggle&&collapse&&!collapse.classList.contains('open'))toggle.click();const a=[...document.querySelectorAll('.dl-btn-lq')].find(x=>/试听音质|标准品质\s*MP3/i.test(x.textContent));if(!a||!a.getClientRects().length)return {found:false};const href=a.getAttribute('href'),url=href&&/^https?:/i.test(href)?a.href:(typeof window.mp3_url==='string'?window.mp3_url:'');return {found:true,url:url,label:a.textContent.trim()};})()";
    }
}
