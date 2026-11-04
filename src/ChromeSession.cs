// YRT-GPT. Normal Chrome in isolated visible or headless mode. No debugging on the user's normal profile.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace YrtMusic
{
    public sealed class ChromeDownload
    {
        public string FullPath { get; internal set; }
        public string SuggestedFilename { get; internal set; }
        public string Url { get; internal set; }
        public long Bytes { get; internal set; }
    }

    public sealed class ChromeSession : IDisposable
    {
        private const int MaximumMessageBytes = 8 * 1024 * 1024;
        private readonly object sync = new object();
        private readonly Dictionary<int, TaskCompletionSource<Dictionary<string, object>>> pending = new Dictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
        private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim downloadLock = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private ClientWebSocket socket;
        private Process browserProcess;
        private FileStream profileLock;
        private Task receiveTask;
        private int nextId;
        private bool disposed;
        private bool started;
        private bool backgroundMode;
        private string pageSession;
        private string pageTarget;
        private string pageUrl = "about:blank";
        private DownloadState activeDownload;

        private sealed class DownloadState
        {
            public string TargetId;
            public string SessionId;
            public string FrameId;
            public string Directory;
            public string Guid;
            public string DocumentRequestId;
            public readonly HashSet<string> ExpectedUrls = new HashSet<string>(StringComparer.Ordinal);
            public string SuggestedFilename;
            public string Url;
            public long Bytes;
            public bool Closing;
            public readonly TaskCompletionSource<bool> Complete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public string PageUrl { get { lock (sync) return pageUrl; } }
        public bool IsBackground { get { lock (sync) return backgroundMode; } }
        public bool IsRunning
        {
            get { lock (sync) return started && !disposed && socket != null && socket.State == WebSocketState.Open; }
        }

        public Task StartAsync(string profileDir, CancellationToken cancellationToken)
        {
            return StartAsync(profileDir, false, cancellationToken);
        }

        public async Task StartAsync(string profileDir, bool background, CancellationToken cancellationToken)
        {
            if (String.IsNullOrWhiteSpace(profileDir)) throw new ArgumentException("必须为本程序指定独立的 Chrome 资料目录。", "profileDir");
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException("ChromeSession");
                if (started || browserProcess != null) throw new InvalidOperationException("Chrome 会话已经启动，请重用当前会话。");
                backgroundMode = background;
            }
            cancellationToken.ThrowIfCancellationRequested();
            string fullProfile = Path.GetFullPath(profileDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string standardProfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data").TrimEnd(Path.DirectorySeparatorChar);
            if (fullProfile.Equals(standardProfile, StringComparison.OrdinalIgnoreCase) || fullProfile.StartsWith(standardProfile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请为本程序使用独立的 Chrome 资料目录，不能使用日常 Chrome 的用户资料。");
            string chrome = FindChrome();
            Directory.CreateDirectory(fullProfile);
            try
            {
                profileLock = new FileStream(Path.Combine(fullProfile, ".yrt-music-session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException("该音乐程序的浏览器资料目录正在使用。请关闭另一个音乐下载器后再试。", ex);
            }
            try
            {
                string portFile = Path.Combine(fullProfile, "DevToolsActivePort");
                if (File.Exists(portFile)) File.Delete(portFile);
                var info = new ProcessStartInfo(chrome,
                    "--remote-debugging-port=0 --remote-debugging-address=127.0.0.1 --user-data-dir=" + QuoteArgument(fullProfile) +
                    " --no-first-run --no-default-browser-check" +
                    (background ? " --headless=new --window-size=1440,1000 about:blank" : " --new-window about:blank"));
                info.UseShellExecute = false;
                info.CreateNoWindow = background;
                if (background) info.WindowStyle = ProcessWindowStyle.Hidden;
                browserProcess = Process.Start(info);
                if (browserProcess == null) throw new InvalidOperationException("无法启动 Google Chrome。");
                string endpoint = null;
                DateTime deadline = DateTime.UtcNow.AddSeconds(25);
                while (DateTime.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (browserProcess.HasExited) throw new InvalidOperationException("独立 Chrome 已退出。资料目录可能正被其他 Chrome 使用，请关闭该独立窗口后重试。");
                    if (File.Exists(portFile))
                    {
                        try
                        {
                            string[] lines = File.ReadAllLines(portFile);
                            int port;
                            if (lines.Length >= 2 && Int32.TryParse(lines[0], out port) && port > 0 && port < 65536 && lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal))
                            {
                                endpoint = "ws://127.0.0.1:" + port + lines[1];
                                break;
                            }
                        }
                        catch (IOException) { }
                    }
                    await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                }
                if (endpoint == null) throw new TimeoutException("Chrome 在 25 秒内没有提供本地连接。请检查独立浏览器是否正常打开。");
                socket = new ClientWebSocket();
                socket.Options.Proxy = null;
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token))
                {
                    linked.CancelAfter(TimeSpan.FromSeconds(15));
                    await socket.ConnectAsync(new Uri(endpoint), linked.Token).ConfigureAwait(false);
                }
                receiveTask = ReceiveLoopAsync();
                var targets = await CommandAsync("Target.getTargets", null, null, cancellationToken).ConfigureAwait(false);
                object[] targetInfos = AsArray(Get(targets, "targetInfos"));
                foreach (object item in targetInfos)
                {
                    var target = AsDictionary(item);
                    if (Text(target, "type") == "page") { pageTarget = Text(target, "targetId"); break; }
                }
                if (String.IsNullOrEmpty(pageTarget))
                    pageTarget = Text(await CommandAsync("Target.createTarget", Dict("url", "about:blank"), null, cancellationToken).ConfigureAwait(false), "targetId");
                var attachment = await CommandAsync("Target.attachToTarget", Dict("targetId", pageTarget, "flatten", true), null, cancellationToken).ConfigureAwait(false);
                pageSession = Text(attachment, "sessionId");
                await CommandAsync("Page.enable", null, pageSession, cancellationToken).ConfigureAwait(false);
                await CommandAsync("Runtime.enable", null, pageSession, cancellationToken).ConfigureAwait(false);
                if (background) await SetDesktopViewportAsync(pageSession, cancellationToken).ConfigureAwait(false);
                await CommandAsync("Browser.setDownloadBehavior", Dict("behavior", "deny", "eventsEnabled", true), null, cancellationToken).ConfigureAwait(false);
                lock (sync) started = true;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public async Task NavigateAsync(string url, CancellationToken cancellationToken)
        {
            RequireRunning();
            ValidateHttpUrl(url);
            lock (sync) pageUrl = url;
            var result = await CommandAsync("Page.navigate", Dict("url", url), pageSession, cancellationToken).ConfigureAwait(false);
            string error = Text(result, "errorText");
            if (!String.IsNullOrEmpty(error)) throw new InvalidOperationException("Chrome 无法打开网页：" + error + "；地址：" + url);
        }

        public Task<object> EvaluateAsync(string javascript, CancellationToken cancellationToken)
        {
            RequireRunning();
            return EvaluateOnSessionAsync(javascript, pageSession, cancellationToken);
        }

        private async Task<object> EvaluateOnSessionAsync(string javascript, string session, CancellationToken cancellationToken)
        {
            var result = await CommandAsync("Runtime.evaluate", Dict("expression", javascript, "returnByValue", true, "awaitPromise", true, "userGesture", true), session, cancellationToken, 45000).ConfigureAwait(false);
            var exception = Get(result, "exceptionDetails") as Dictionary<string, object>;
            if (exception != null)
            {
                var details = Get(exception, "exception") as Dictionary<string, object>;
                string description = details == null ? Text(exception, "text") : Text(details, "description");
                throw new InvalidOperationException("网页执行失败：" + description);
            }
            var remote = AsDictionary(Get(result, "result"));
            return Get(remote, "value");
        }

        public async Task BringToFrontAsync(CancellationToken cancellationToken)
        {
            RequireRunning();
            cancellationToken.ThrowIfCancellationRequested();
            if (IsBackground) return;
            await CommandAsync("Page.bringToFront", null, pageSession, cancellationToken).ConfigureAwait(false);
        }

        private Task<Dictionary<string, object>> SetDesktopViewportAsync(string session, CancellationToken cancellationToken)
        {
            // Desktop layout only. The real Chrome user agent and normal request headers are unchanged.
            return CommandAsync("Emulation.setDeviceMetricsOverride", Dict("width", 1440, "height", 1000,
                "deviceScaleFactor", 1, "mobile", false, "screenWidth", 1440, "screenHeight", 1000), session, cancellationToken);
        }

        public async Task<ChromeDownload> DownloadAsync(string url, string destinationDirectory, CancellationToken cancellationToken)
        {
            RequireRunning();
            ValidateHttpUrl(url);
            if (String.IsNullOrWhiteSpace(destinationDirectory)) throw new ArgumentException("下载目录不能为空。", "destinationDirectory");
            await downloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            DownloadState state = null;
            ChromeDownload completed = null;
            Exception failure = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination = Path.GetFullPath(destinationDirectory);
                Directory.CreateDirectory(destination);
                state = new DownloadState();
                state.Directory = Path.Combine(destination, ".yrt-chrome-" + System.Guid.NewGuid().ToString("N"));
                state.Url = url;
                state.ExpectedUrls.Add(NormalizeDownloadUrl(url));
                Directory.CreateDirectory(state.Directory);
                state.TargetId = Text(await CommandAsync("Target.createTarget", Dict("url", "about:blank"), null, cancellationToken).ConfigureAwait(false), "targetId");
                state.SessionId = Text(await CommandAsync("Target.attachToTarget", Dict("targetId", state.TargetId, "flatten", true), null, cancellationToken).ConfigureAwait(false), "sessionId");
                await CommandAsync("Page.enable", null, state.SessionId, cancellationToken).ConfigureAwait(false);
                await CommandAsync("Runtime.enable", null, state.SessionId, cancellationToken).ConfigureAwait(false);
                if (IsBackground) await SetDesktopViewportAsync(state.SessionId, cancellationToken).ConfigureAwait(false);
                await CommandAsync("Network.enable", null, state.SessionId, cancellationToken).ConfigureAwait(false);
                var tree = await CommandAsync("Page.getFrameTree", null, state.SessionId, cancellationToken).ConfigureAwait(false);
                state.FrameId = Text(AsDictionary(Get(AsDictionary(Get(tree, "frameTree")), "frame")), "id");
                if (String.IsNullOrEmpty(state.FrameId)) throw new InvalidOperationException("Chrome 没有返回下载页标识，无法安全关联下载。");
                lock (sync) activeDownload = state;
                await CommandAsync("Browser.setDownloadBehavior", Dict("behavior", "allowAndName", "downloadPath", state.Directory, "eventsEnabled", true), null, cancellationToken).ConfigureAwait(false);
                var navigation = await CommandAsync("Page.navigate", Dict("url", url), state.SessionId, cancellationToken).ConfigureAwait(false);
                bool isDownload = Get(navigation, "isDownload") is bool && (bool)Get(navigation, "isDownload");
                string navigationError = Text(navigation, "errorText");
                if (!String.IsNullOrEmpty(navigationError) && navigationError != "net::ERR_ABORTED")
                    throw new InvalidOperationException("Chrome 下载链接访问失败：" + navigationError);
                if (!isDownload)
                {
                    // Chrome opens inline audio in its normal media viewer. A same-origin download link
                    // in that document is a normal browser operation; no response interception or CORS bypass.
                    await TriggerInlineDownloadAsync(state, cancellationToken).ConfigureAwait(false);
                }
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromMinutes(5));
                    using (timeout.Token.Register(delegate { state.Complete.TrySetCanceled(); }))
                    {
                        try { await state.Complete.Task.ConfigureAwait(false); }
                        catch (OperationCanceledException)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (lifetime.IsCancellationRequested) throw new InvalidOperationException("下载期间 Chrome 会话已关闭。");
                            throw new TimeoutException("Chrome 在 5 分钟内未完成下载。链接可能只提供在线播放、需要网页确认，或已失效。");
                        }
                    }
                }
                string source = Path.Combine(state.Directory, state.Guid);
                DateTime fileDeadline = DateTime.UtcNow.AddSeconds(5);
                while (!File.Exists(source) && DateTime.UtcNow < fileDeadline)
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                if (!File.Exists(source)) throw new IOException("Chrome 已报告完成，但本次下载文件不存在。");
                var file = new FileInfo(source);
                if (file.Length == 0) throw new IOException("下载文件为空，未作为成功下载保存。");
                string safeName = SafeFilename(state.SuggestedFilename);
                string resultPath = MoveWithoutOverwrite(source, destination, safeName);
                completed = new ChromeDownload { FullPath = resultPath, SuggestedFilename = state.SuggestedFilename, Url = state.Url, Bytes = new FileInfo(resultPath).Length };
            }
            catch (Exception ex) { failure = ex; }
            try
            {
                if (state != null)
                {
                    lock (sync) state.Closing = true;
                    if (!String.IsNullOrEmpty(state.Guid) && !state.Complete.Task.IsCompleted)
                        await BestEffortCommandAsync("Browser.cancelDownload", Dict("guid", state.Guid), null).ConfigureAwait(false);
                    // Cancellation also marks Complete canceled, so ask Chrome to stop unless it really completed.
                    if (!String.IsNullOrEmpty(state.Guid) && (state.Complete.Task.IsCanceled || state.Complete.Task.IsFaulted))
                        await BestEffortCommandAsync("Browser.cancelDownload", Dict("guid", state.Guid), null).ConfigureAwait(false);
                    if (!String.IsNullOrEmpty(state.TargetId))
                        await BestEffortCommandAsync("Target.closeTarget", Dict("targetId", state.TargetId), null).ConfigureAwait(false);
                    await BestEffortCommandAsync("Browser.setDownloadBehavior", Dict("behavior", "deny", "eventsEnabled", true), null).ConfigureAwait(false);
                    lock (sync) { if (Object.ReferenceEquals(activeDownload, state)) activeDownload = null; }
                    await CleanupDownloadAsync(state).ConfigureAwait(false);
                }
            }
            finally { downloadLock.Release(); }
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return completed;
        }

        private async Task TriggerInlineDownloadAsync(DownloadState state, CancellationToken cancellationToken)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            Exception last = null;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (sync) { if (!String.IsNullOrEmpty(state.Guid)) return; }
                try
                {
                    object ready = await EvaluateOnSessionAsync("(function(){if(document.readyState==='loading'||location.protocol!=='http:'&&location.protocol!=='https:')return false;var a=document.createElement('a');a.href=location.href;a.download='';document.body.appendChild(a);a.click();a.remove();return true;})()", state.SessionId, cancellationToken).ConfigureAwait(false);
                    if (ready is bool && (bool)ready) return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { last = ex; }
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
            throw new InvalidOperationException("链接未进入可下载网页。请在独立 Chrome 中确认该链接能正常打开。" + (last == null ? "" : " " + last.Message), last);
        }

        private async Task CleanupDownloadAsync(DownloadState state)
        {
            // Never recursively delete a destination directory or remove any unrelated files.
            for (int attempt = 0; attempt < 12; attempt++)
            {
                bool retry = false;
                if (!String.IsNullOrEmpty(state.Guid))
                {
                    foreach (string suffix in new[] { "", ".crdownload" })
                    {
                        string path = Path.Combine(state.Directory, state.Guid + suffix);
                        try { if (File.Exists(path)) File.Delete(path); }
                        catch (IOException) { retry = true; }
                        catch (UnauthorizedAccessException) { retry = true; }
                    }
                }
                try { if (Directory.Exists(state.Directory) && Directory.GetFileSystemEntries(state.Directory).Length == 0) Directory.Delete(state.Directory, false); }
                catch (IOException) { retry = true; }
                catch (UnauthorizedAccessException) { retry = true; }
                if (!retry) return;
                await Task.Delay(150).ConfigureAwait(false);
            }
        }

        private async Task<Dictionary<string, object>> CommandAsync(string method, Dictionary<string, object> parameters, string session, CancellationToken cancellationToken, int timeoutMilliseconds = 20000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (socket == null || socket.State != WebSocketState.Open) throw new InvalidOperationException("独立 Chrome 连接已断开，请重新打开程序的浏览器。");
            int id = Interlocked.Increment(ref nextId);
            var completion = new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var command = Dict("id", id, "method", method);
            if (parameters != null) command["params"] = parameters;
            if (!String.IsNullOrEmpty(session)) command["sessionId"] = session;
            byte[] data = Encoding.UTF8.GetBytes(NewSerializer().Serialize(command));
            lock (sync) pending.Add(id, completion);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token))
            {
                timeout.CancelAfter(timeoutMilliseconds);
                using (timeout.Token.Register(delegate { completion.TrySetCanceled(); }))
                {
                    try
                    {
                        await sendLock.WaitAsync(timeout.Token).ConfigureAwait(false);
                        try
                        {
                            timeout.Token.ThrowIfCancellationRequested();
                            // Cancel a caller's pending command, not the shared websocket transport.
                            // Interrupting ClientWebSocket.SendAsync can abort all unrelated commands.
                            using (var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                            {
                                sendTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                                await socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, sendTimeout.Token).ConfigureAwait(false);
                            }
                        }
                        finally { sendLock.Release(); }
                        var response = await completion.Task.ConfigureAwait(false);
                        var error = Get(response, "error") as Dictionary<string, object>;
                        if (error != null) throw new InvalidOperationException("Chrome " + method + "：" + Text(error, "message"));
                        return AsDictionary(Get(response, "result"));
                    }
                    catch (OperationCanceledException)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (lifetime.IsCancellationRequested) throw new InvalidOperationException("独立 Chrome 会话已经关闭。");
                        throw new TimeoutException("Chrome 操作超时：" + method);
                    }
                    finally { lock (sync) pending.Remove(id); }
                }
            }
        }

        private async Task ReceiveLoopAsync()
        {
            Exception failure = null;
            try
            {
                byte[] buffer = new byte[16384];
                while (!lifetime.IsCancellationRequested)
                {
                    using (var message = new MemoryStream())
                    {
                        WebSocketReceiveResult part;
                        do
                        {
                            part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), lifetime.Token).ConfigureAwait(false);
                            if (part.MessageType == WebSocketMessageType.Close) throw new IOException("独立 Chrome 窗口已经关闭。");
                            if (part.MessageType != WebSocketMessageType.Text) throw new IOException("Chrome 返回了不支持的消息格式。");
                            if (message.Length + part.Count > MaximumMessageBytes) throw new IOException("Chrome 返回的数据超过 8 MB 限制，请缩小本次网页读取范围。");
                            message.Write(buffer, 0, part.Count);
                        } while (!part.EndOfMessage);
                        string json = new UTF8Encoding(false, true).GetString(message.ToArray());
                        var content = AsDictionary(NewSerializer().DeserializeObject(json));
                        object idValue = Get(content, "id");
                        if (idValue != null)
                        {
                            TaskCompletionSource<Dictionary<string, object>> completion;
                            lock (sync) pending.TryGetValue(Convert.ToInt32(idValue), out completion);
                            if (completion != null) completion.TrySetResult(content);
                        }
                        else DispatchEvent(content);
                    }
                }
            }
            catch (OperationCanceledException) { failure = new IOException("独立 Chrome 会话已关闭。"); }
            catch (Exception ex) { failure = new IOException("独立 Chrome 连接中断：" + ex.Message, ex); }
            finally
            {
                lock (sync)
                {
                    started = false;
                    foreach (var item in pending.Values) item.TrySetException(failure ?? new IOException("独立 Chrome 连接结束。"));
                    if (activeDownload != null) activeDownload.Complete.TrySetException(failure ?? new IOException("下载期间独立 Chrome 连接结束。"));
                }
            }
        }

        private void DispatchEvent(Dictionary<string, object> content)
        {
            string method = Text(content, "method");
            var parameters = AsDictionary(Get(content, "params"));
            if (method == "Page.frameNavigated" && Text(content, "sessionId") == pageSession)
            {
                var frame = AsDictionary(Get(parameters, "frame"));
                if (String.IsNullOrEmpty(Text(frame, "parentId"))) lock (sync) pageUrl = Text(frame, "url");
            }
            else if (method == "Network.requestWillBeSent")
            {
                lock (sync)
                {
                    if (activeDownload != null && Text(content, "sessionId") == activeDownload.SessionId && Text(parameters, "frameId") == activeDownload.FrameId && Text(parameters, "type") == "Document")
                    {
                        string requestId = Text(parameters, "requestId");
                        string requestUrl = NormalizeDownloadUrl(Text(AsDictionary(Get(parameters, "request")), "url"));
                        if (String.IsNullOrEmpty(activeDownload.DocumentRequestId) && activeDownload.ExpectedUrls.Contains(requestUrl))
                            activeDownload.DocumentRequestId = requestId;
                        if (!String.IsNullOrEmpty(requestId) && requestId == activeDownload.DocumentRequestId)
                            activeDownload.ExpectedUrls.Add(requestUrl);
                    }
                }
            }
            else if (method == "Browser.downloadWillBegin")
            {
                string guid = Text(parameters, "guid");
                bool accept = false;
                lock (sync)
                {
                    if (activeDownload != null && !activeDownload.Closing && !activeDownload.Complete.Task.IsCompleted && String.IsNullOrEmpty(activeDownload.Guid) && Text(parameters, "frameId") == activeDownload.FrameId && activeDownload.ExpectedUrls.Contains(NormalizeDownloadUrl(Text(parameters, "url"))))
                    {
                        activeDownload.Guid = guid;
                        activeDownload.SuggestedFilename = Text(parameters, "suggestedFilename");
                        activeDownload.Url = Text(parameters, "url");
                        accept = true;
                    }
                }
                if (!accept && !String.IsNullOrEmpty(guid))
                {
                    // Keep only the explicitly requested download in this program's isolated browser.
                    Task ignored = BestEffortCommandAsync("Browser.cancelDownload", Dict("guid", guid), null);
                }
            }
            else if (method == "Browser.downloadProgress")
            {
                lock (sync)
                {
                    if (activeDownload == null || Text(parameters, "guid") != activeDownload.Guid) return;
                    object bytes = Get(parameters, "receivedBytes");
                    if (bytes != null) activeDownload.Bytes = Convert.ToInt64(bytes);
                    string status = Text(parameters, "state");
                    if (status == "completed") activeDownload.Complete.TrySetResult(true);
                    else if (status == "canceled") activeDownload.Complete.TrySetException(new IOException("Chrome 取消了本次下载。可能是链接失效、浏览器安全检查或用户取消。请查看独立 Chrome 的下载提示。"));
                }
            }
        }

        private async Task BestEffortCommandAsync(string method, Dictionary<string, object> parameters, string session)
        {
            try { await CommandAsync(method, parameters, session, CancellationToken.None, 2500).ConfigureAwait(false); }
            catch { }
        }

        private void RequireRunning()
        {
            if (disposed) throw new ObjectDisposedException("ChromeSession");
            if (!IsRunning) throw new InvalidOperationException("请先启动本程序的独立 Chrome 浏览器。");
        }

        public void Dispose()
        {
            lock (sync) { if (disposed) return; disposed = true; }
            try
            {
                if (socket != null && socket.State == WebSocketState.Open)
                    BestEffortCommandAsync("Browser.close", null, null).GetAwaiter().GetResult();
            }
            catch { }
            lifetime.Cancel();
            try { if (socket != null) socket.Abort(); } catch { }
            try { if (socket != null) socket.Dispose(); } catch { }
            if (browserProcess != null)
            {
                try { if (!browserProcess.HasExited && !browserProcess.WaitForExit(1500)) browserProcess.Kill(); } catch { }
                browserProcess.Dispose();
            }
            if (profileLock != null) profileLock.Dispose();
        }

        private static string FindChrome()
        {
            var candidates = new List<string>();
            foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using (var root = RegistryKey.OpenBaseKey(hive, view))
                        using (var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"))
                            if (key != null && key.GetValue(null) is string) candidates.Add(((string)key.GetValue(null)).Trim('"'));
                    }
                    catch (System.Security.SecurityException) { }
                    catch (UnauthorizedAccessException) { }
                }
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"));
            foreach (string candidate in candidates) if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            throw new FileNotFoundException("未找到 Google Chrome。请先安装 Chrome；本程序不会安装或接管其他浏览器。");
        }

        private static string NormalizeDownloadUrl(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri)) return value ?? "";
            var builder = new UriBuilder(uri);
            builder.Fragment = "";
            return builder.Uri.AbsoluteUri;
        }

        private static void ValidateHttpUrl(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http") || !String.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException("只支持正常的 HTTP/HTTPS 网页或下载链接，不能包含账号密码。", "value");
        }

        private static string QuoteArgument(string value)
        {
            // Directory paths cannot contain quotes on Windows; no shell is involved.
            if (value.IndexOf('"') >= 0) throw new ArgumentException("浏览器资料目录含有无效字符。");
            return "\"" + value.TrimEnd('\\') + "\"";
        }

        private static string SafeFilename(string suggested)
        {
            if (String.IsNullOrWhiteSpace(suggested)) suggested = "下载文件.bin";
            suggested = suggested.Replace('/', '_').Replace('\\', '_');
            foreach (char invalid in Path.GetInvalidFileNameChars()) suggested = suggested.Replace(invalid, '_');
            suggested = suggested.Trim().TrimEnd('.', ' ');
            if (suggested.Length == 0) suggested = "下载文件.bin";
            if (suggested.Length > 150)
            {
                string extension = Path.GetExtension(suggested);
                if (extension.Length > 16) extension = ".bin";
                suggested = suggested.Substring(0, 130) + extension;
            }
            string stem = suggested.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem.StartsWith("COM", StringComparison.Ordinal) && stem.Length == 4 && Char.IsDigit(stem[3]) || stem.StartsWith("LPT", StringComparison.Ordinal) && stem.Length == 4 && Char.IsDigit(stem[3])) suggested = "_" + suggested;
            return suggested;
        }

        private static string MoveWithoutOverwrite(string source, string destination, string filename)
        {
            string extension = Path.GetExtension(filename);
            string stem = Path.GetFileNameWithoutExtension(filename);
            for (int suffix = 0; suffix < 10000; suffix++)
            {
                string target = Path.Combine(destination, suffix == 0 ? filename : stem + " (" + suffix + ")" + extension);
                try { File.Move(source, target); return target; }
                catch (IOException) { if (!File.Exists(target)) throw; }
            }
            throw new IOException("下载目录中的同名文件过多，请整理后重试。");
        }

        private static JavaScriptSerializer NewSerializer() { return new JavaScriptSerializer { MaxJsonLength = MaximumMessageBytes, RecursionLimit = 100 }; }
        private static Dictionary<string, object> Dict(params object[] pairs)
        {
            var result = new Dictionary<string, object>();
            for (int i = 0; i < pairs.Length; i += 2) result.Add((string)pairs[i], pairs[i + 1]);
            return result;
        }
        private static Dictionary<string, object> AsDictionary(object value) { return value as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        private static object Get(Dictionary<string, object> value, string key) { object result; return value != null && value.TryGetValue(key, out result) ? result : null; }
        private static string Text(Dictionary<string, object> value, string key) { object result = Get(value, key); return result == null ? "" : Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture); }
        private static object[] AsArray(object value)
        {
            var array = value as object[];
            if (array != null) return array;
            var list = value as ArrayList;
            return list == null ? new object[0] : list.ToArray();
        }
    }
}
