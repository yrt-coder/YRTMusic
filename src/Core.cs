using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace YrtMusic
{
    public sealed class BatchRunner : IDisposable
    {
        private const string Home = "https://www.gequhai.com/";
        private readonly string dataDir, outputDirectory;
        private readonly SemaphoreSlim browserLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim runLock = new SemaphoreSlim(1, 1);
        private readonly object pauseLock = new object(), logLock = new object();
        private ChromeSession browser;
        private TaskCompletionSource<bool> resume;
        private bool disposed;
        public event Action<string> Log;
        public event Action<MusicJob> JobChanged;
        public event Action<string> NeedsUser;
        public bool IsPaused { get { lock (pauseLock) return resume != null; } }

        public BatchRunner(string dataDirectory, string musicDirectory)
        {
            dataDir = Path.GetFullPath(dataDirectory);
            outputDirectory = Path.GetFullPath(musicDirectory);
            Directory.CreateDirectory(dataDir);
        }
        public async Task OpenBrowserAsync(CancellationToken token)
        {
            await EnsureBrowserAsync(token).ConfigureAwait(false);
            if (browser.PageUrl == "about:blank") await browser.NavigateAsync(Home, token).ConfigureAwait(false);
            await browser.BringToFrontAsync(token).ConfigureAwait(false);
        }
        private async Task EnsureBrowserAsync(CancellationToken token)
        {
            await browserLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (disposed) throw new ObjectDisposedException("BatchRunner");
                if (browser != null && browser.IsRunning) return;
                if (browser != null) browser.Dispose();
                browser = new ChromeSession();
                await browser.StartAsync(Path.Combine(dataDir, "chrome-profile"), token).ConfigureAwait(false);
                WriteLog("Chrome 已打开，使用本程序的独立浏览器窗口。");
            }
            finally { browserLock.Release(); }
        }
        public async Task RunAsync(IList<MusicJob> jobs, CancellationToken token)
        {
            if (!await runLock.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException("已有一批任务正在运行。");
            try
            {
                await EnsureBrowserAsync(token).ConfigureAwait(false);
                Directory.CreateDirectory(outputDirectory);
                var seenPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                WriteLog("开始逐项搜索并下载试听 MP3，共 " + jobs.Count + " 项。保存位置：" + outputDirectory);
                foreach (MusicJob job in jobs)
                {
                    token.ThrowIfCancellationRequested();
                    if (job.Query.Length > 50)
                    {
                        Change(job, "未匹配", "网站搜索最多 50 字，请缩短这一行。");
                        continue;
                    }
                    try
                    {
                        bool complete = false;
                        while (!complete)
                        {
                            string pauseMessage = null;
                            try
                            {
                                await ProcessAsync(job, seenPages, token).ConfigureAwait(false);
                                complete = true;
                            }
                            catch (WebsitePauseException ex) { pauseMessage = ex.Message; }
                            if (pauseMessage != null)
                            {
                                await WaitForUserAsync(job, pauseMessage, token).ConfigureAwait(false);
                                await EnsureBrowserAsync(token).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (OperationCanceledException) { Change(job, "已停止", "本次操作已取消。"); throw; }
                    catch (Exception ex)
                    {
                        Change(job, "失败", ex.Message);
                        WriteLog("第 " + job.Index + " 项未完成：" + job.Query + "；" + ex.Message);
                    }
                    await Task.Delay(2000, token).ConfigureAwait(false);
                }
                WriteLog("本批处理结束。请以每首歌曲的状态和实际文件为准。");
            }
            catch (OperationCanceledException)
            {
                foreach (MusicJob job in jobs) if (job.State == "待处理") Change(job, "已停止", "未开始；本批已停止。");
                WriteLog("任务已停止，已完成的音乐文件已保留。");
                throw;
            }
            finally
            {
                lock (pauseLock) resume = null;
                runLock.Release();
            }
        }
        private async Task ProcessAsync(MusicJob job, HashSet<string> seenPages, CancellationToken token)
        {
            Change(job, "搜索中", "正在 Chrome 中打开歌曲海。");
            await browser.NavigateAsync(Home, token).ConfigureAwait(false);
            await WaitPageAsync(s => Flag(s, "hasInput"), token).ConfigureAwait(false);
            string query = new JavaScriptSerializer().Serialize(job.Query);
            string searchScript = "(()=>{const i=document.querySelector('#s-input,#s-input-line');const b=document.querySelector('#s-btn,#s-btn-line');if(!i||!b)return false;i.value=" + query + ";i.dispatchEvent(new Event('input',{bubbles:true}));i.dispatchEvent(new Event('change',{bubbles:true}));b.click();return true;})()";
            object submitted = await browser.EvaluateAsync(searchScript, token).ConfigureAwait(false);
            if (!(submitted is bool) || !(bool)submitted) throw new WebsitePauseException("网页搜索控件发生变化。请在 Chrome 检查页面，处理后点击“继续任务”。");
            Dictionary<string, object> results = await WaitPageAsync(s => Text(s, "path") == "/search" && Text(s, "searchKey") == job.Query, token).ConfigureAwait(false);
            List<SongCandidate> candidates = ReadCandidates(results);
            if (candidates.Count == 0) { Change(job, "未匹配", "网站没有返回相关歌曲。"); return; }
            SongCandidate best = SongTools.SelectBest(job.Query, candidates);
            if (best == null) { Change(job, "未匹配", "没有足够匹配的版本；可使用“歌名 - 歌手”缩小范围。"); return; }
            job.Title = best.Title; job.Artist = best.Artist; job.SourcePage = best.Url;
            if (seenPages.Contains(best.Url)) { Change(job, "已跳过", "本批已经下载了同一首、同一来源的歌曲。"); return; }
            Change(job, "已匹配", "匹配度 " + (best.Score * 100).ToString("0", CultureInfo.InvariantCulture) + "% · 正在获取试听下载入口。");
            await browser.NavigateAsync(best.Url, token).ConfigureAwait(false);
            await WaitPageAsync(s => Flag(s, "hasDownload"), token).ConfigureAwait(false);
            await browser.EvaluateAsync("(()=>{const b=document.querySelector('#btn-download-mp3');if(!b)return false;b.click();return true;})()", token).ConfigureAwait(false);
            Dictionary<string, object> quality = await WaitQualityAsync(token).ConfigureAwait(false);
            string mediaUrl = Text(quality, "url");
            Uri uri;
            if (!Uri.TryCreate(mediaUrl, UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http") || !String.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidDataException("网站没有提供可直接下载的公开试听链接。");
            Change(job, "下载中", "试听音质 MP3，等待 Chrome 完成下载。");
            string incoming = Path.Combine(dataDir, "incoming");
            ChromeDownload download = null;
            try
            {
                download = await browser.DownloadAsync(mediaUrl, incoming, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                string reason;
                if (!SongTools.IsMp3(download.FullPath, out reason)) throw new InvalidDataException("下载内容不是有效 MP3：" + reason);
                token.ThrowIfCancellationRequested();
                job.FilePath = SongTools.MoveToMusic(download.FullPath, outputDirectory, best.Title, best.Artist);
                seenPages.Add(best.Url);
                Change(job, "已完成", "已保存：" + Path.GetFileName(job.FilePath));
                WriteLog("已下载：" + best.Title + " - " + best.Artist + " → " + job.FilePath);
            }
            finally
            {
                if (download != null && File.Exists(download.FullPath))
                {
                    string full = Path.GetFullPath(download.FullPath);
                    string prefix = Path.GetFullPath(incoming).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) try { File.Delete(full); } catch (IOException) { }
                }
            }
        }
        private async Task<Dictionary<string, object>> WaitPageAsync(Func<Dictionary<string, object>, bool> ready, CancellationToken token)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(40);
            string last = "页面尚未准备好。";
            while (DateTime.UtcNow < until)
            {
                token.ThrowIfCancellationRequested();
                Dictionary<string, object> page = null;
                try { page = AsDict(await browser.EvaluateAsync(PageSnapshot, token).ConfigureAwait(false)); }
                catch (InvalidOperationException ex) { last = ex.Message; }
                if (page != null)
                {
                    CheckPage(page);
                    if (Text(page, "ready") == "complete" && ready(page)) return page;
                    last = Text(page, "title");
                }
                await Task.Delay(400, token).ConfigureAwait(false);
            }
            throw new WebsitePauseException("网站页面未能在 40 秒内准备好：" + last + "。请检查 Chrome，处理后点击“继续任务”，也可以停止。");
        }
        private async Task<Dictionary<string, object>> WaitQualityAsync(CancellationToken token)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < until)
            {
                var page = AsDict(await browser.EvaluateAsync(PageSnapshot, token).ConfigureAwait(false));
                CheckPage(page);
                var value = AsDict(await browser.EvaluateAsync(QualitySnapshot, token).ConfigureAwait(false));
                if (value != null && Flag(value, "found") && !String.IsNullOrWhiteSpace(Text(value, "url"))) return value;
                await Task.Delay(400, token).ConfigureAwait(false);
            }
            throw new InvalidDataException("网站未提供可用的“试听音质 MP3”入口；本项未下载。请稍后在 Chrome 核对。");
        }
        private static void CheckPage(Dictionary<string, object> page)
        {
            string text = Text(page, "text");
            string[] markers = { "访问异常", "请求过于频繁", "访问过于频繁", "人机验证", "验证码", "Forbidden", "Access denied", "Just a moment", "Verify you are human", "安全验证", "429 Too Many" };
            foreach (string marker in markers) if (text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                throw new WebsitePauseException("网站显示“" + marker + "”。请在 Chrome 中正常处理，完成后点击“继续任务”。");
            Uri uri;
            if (!Uri.TryCreate(Text(page, "url"), UriKind.Absolute, out uri) || !(uri.Host == "www.gequhai.com" || uri.Host == "gequhai.com"))
                throw new WebsitePauseException("Chrome 当前未显示歌曲海正常页面。请检查网络与网页，再点击“继续任务”。");
        }
        private async Task WaitForUserAsync(MusicJob job, string message, CancellationToken token)
        {
            var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (pauseLock) resume = waiting;
            Change(job, "等待处理", message);
            WriteLog(message);
            Action<string> handler = NeedsUser; if (handler != null) handler(message);
            try
            {
                await browser.BringToFrontAsync(token).ConfigureAwait(false);
                using (token.Register(() => waiting.TrySetCanceled())) await waiting.Task.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
            finally { lock (pauseLock) if (resume == waiting) resume = null; }
        }
        public void Continue() { lock (pauseLock) if (resume != null) resume.TrySetResult(true); }
        private void Change(MusicJob job, string state, string detail)
        {
            job.State = state; job.Detail = detail;
            Action<MusicJob> handler = JobChanged; if (handler != null) handler(job);
            try
            {
                string line = new JavaScriptSerializer().Serialize(new { time = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("o"), index = job.Index, query = job.Query, title = job.Title, artist = job.Artist, state = state, detail = detail, file = job.FilePath, page = job.SourcePage });
                lock (logLock) File.AppendAllText(Path.Combine(dataDir, "tasks.jsonl"), line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException) { }
        }
        private void WriteLog(string message)
        {
            string line = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss zzz") + "  " + message;
            Action<string> handler = Log; if (handler != null) handler(line);
            try { lock (logLock) File.AppendAllText(Path.Combine(dataDir, "run.log"), line + Environment.NewLine, Encoding.UTF8); } catch (IOException) { }
        }
        private static List<SongCandidate> ReadCandidates(Dictionary<string, object> page)
        {
            var result = new List<SongCandidate>();
            object items; if (page == null || !page.TryGetValue("rows", out items)) return result;
            var rows = items as IEnumerable; if (rows == null) return result;
            foreach (object row in rows)
            {
                var d = AsDict(row); Uri uri; string url = Text(d, "url");
                if (Uri.TryCreate(url, UriKind.Absolute, out uri) && (uri.Host == "www.gequhai.com" || uri.Host == "gequhai.com") && System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, @"^/play/\d+/?$"))
                    result.Add(new SongCandidate { Title = Text(d, "title"), Artist = Text(d, "artist"), Url = url });
            }
            return result;
        }
        private static Dictionary<string, object> AsDict(object value) { return value as Dictionary<string, object>; }
        private static string Text(Dictionary<string, object> d, string key) { object v; return d != null && d.TryGetValue(key, out v) && v != null ? v.ToString() : ""; }
        private static bool Flag(Dictionary<string, object> d, string key) { object v; return d != null && d.TryGetValue(key, out v) && v is bool && (bool)v; }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            lock (pauseLock) if (resume != null) resume.TrySetCanceled();
            if (browser != null) browser.Dispose();
        }
        private sealed class WebsitePauseException : Exception { public WebsitePauseException(string text) : base(text) { } }

        // These selectors are from the actual site opened normally in Chrome on 2026-10-23.
        private const string PageSnapshot = @"(()=>({url:location.href,path:location.pathname,title:document.title,ready:document.readyState,text:(document.body?document.body.innerText:'').slice(0,14000),hasInput:!!document.querySelector('#s-input,#s-input-line'),hasDownload:!!document.querySelector('#btn-download-mp3'),searchKey:(document.querySelector('#s-input-line')||{}).value||'',rows:[...document.querySelectorAll('table tbody tr')].map(tr=>{const a=tr.querySelector('a[href^=""/play/""]'),td=tr.querySelectorAll('td');return a?{title:a.textContent.trim(),artist:td.length>=3?td[2].textContent.trim():'',url:a.href}:null}).filter(Boolean)}))()";
        private const string QualitySnapshot = @"(()=>{const toggle=document.querySelector('#dl-toggle'),collapse=document.querySelector('#dl-collapse');if(toggle&&collapse&&!collapse.classList.contains('open'))toggle.click();const a=[...document.querySelectorAll('.dl-btn-lq')].find(x=>/试听音质|标准品质\s*MP3/i.test(x.textContent));if(!a||!a.getClientRects().length)return {found:false};const href=a.getAttribute('href');const url=href&&/^https?:/i.test(href)?a.href:(typeof window.mp3_url==='string'?window.mp3_url:'');return {found:true,url:url,label:a.textContent.trim()};})()";
    }
}
