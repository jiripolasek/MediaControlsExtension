using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace BrowserCompanion;

internal sealed class MediaServer
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Browser> _browsers = [];
    private readonly HashSet<Channel<bool>> _watchers = [];
    private long _revision;
    private int _clients;
    private long _lastActivity = Stopwatch.GetTimestamp();

    internal async Task RunAsync(string pipeName, TimeSpan? idleTimeout = null)
    {
        using var lifetime = new CancellationTokenSource();
        var maintenance = this.ExpireBrowsersAsync(lifetime, idleTimeout);
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 64, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await pipe.WaitForConnectionAsync(lifetime.Token);
                    lock (this._gate)
                    {
                        if (lifetime.IsCancellationRequested)
                        {
                            pipe.Dispose();
                            break;
                        }
                        this._clients++;
                    }
                    _ = this.HandleAsync(pipe);
                }
                catch
                {
                    pipe.Dispose();
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            await lifetime.CancelAsync();
            await maintenance;
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        await using var ownedPipe = pipe;
        Browser? browser = null;
        try
        {
            using var handshakeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var hello = await Wire.ReadAsync(pipe, handshakeTimeout.Token) ?? throw new EndOfStreamException();
            if (Wire.Text(hello, "type") == "browser")
            {
                var greeting = await Wire.ReadAsync(pipe, handshakeTimeout.Token) ?? throw new EndOfStreamException();
                var epoch = Wire.Text(greeting, "epoch");
                if (Wire.Text(greeting, "type") != "hello" || greeting["version"]?.GetValue<int>() != 1 ||
                    !Guid.TryParse(epoch, out _) || !Guid.TryParse(Wire.Text(greeting, "profileId"), out _))
                {
                    throw new InvalidDataException("Invalid browser handshake.");
                }

                browser = new Browser(pipe, greeting, Wire.Text(hello, "applicationId"), Wire.Text(hello, "browserExecutable"));
                var browserName = Wire.Text(hello, "browserName");
                if (browserName.Length > 0) greeting["browser"] = browserName[..Math.Min(browserName.Length, 128)];
                Browser[] replaced;
                lock (this._gate)
                {
                    replaced = this._browsers.Values.Where(previous => previous.ApplicationId == browser.ApplicationId &&
                        (browser.ApplicationId.Length > 0 || string.Equals(previous.ExecutablePath, browser.ExecutablePath, StringComparison.OrdinalIgnoreCase)) &&
                        Wire.Text(previous.Hello, "profileId") == Wire.Text(greeting, "profileId")).ToArray();
                    foreach (var previous in replaced)
                    {
                        this._browsers.Remove(previous.Id);
                        foreach (var pending in previous.Pending.Values) pending.TrySetResult(Wire.Status("session-gone"));
                    }
                    this._browsers.Add(browser.Id, browser);
                    this.Changed();
                }
                foreach (var previous in replaced) previous.Disconnect();

                await browser.SendAsync(new JsonObject { ["type"] = "ready", ["connectionId"] = browser.Id });
                while (await Wire.ReadAsync(pipe) is { } message)
                {
                    lock (this._gate)
                    {
                        if (!this._browsers.ContainsKey(browser.Id)) break;
                        if (Wire.Text(message, "type") == "heartbeat" && Wire.Text(message, "epoch") == browser.Epoch)
                        {
                            browser.LastSeen = Stopwatch.GetTimestamp();
                            browser.HasHeartbeat = true;
                        }
                        if (Wire.Text(message, "type") == "snapshot")
                        {
                            var revision = message["revision"]?.GetValue<long>() ?? 0;
                            if (Wire.Text(message, "epoch") != browser.Epoch || revision <= browser.Revision)
                            {
                                continue;
                            }

                            var sessions = message["sessions"] as JsonArray ?? throw new InvalidDataException("Missing sessions.");
                            if (sessions.Count > 256 || sessions.Any(item => item is not JsonObject ||
                                string.IsNullOrWhiteSpace(item["id"]?.GetValue<string>())))
                            {
                                throw new InvalidDataException("Invalid session list.");
                            }

                            if (sessions.Select(item => item!["id"]!.GetValue<string>()).Distinct().Count() != sessions.Count)
                            {
                                throw new InvalidDataException("Duplicate session identity.");
                            }

                            browser.Sessions = (JsonArray)sessions.DeepClone();
                            browser.Revision = revision;
                            browser.UpdateTabs();
                            this.Changed();
                        }
                        else if (Wire.Text(message, "type") == "result" &&
                            browser.Pending.TryGetValue(Wire.Text(message, "requestId"), out var completion))
                        {
                            completion.TrySetResult((JsonObject)message.DeepClone());
                        }
                    }
                }
            }
            else if (Wire.Text(hello, "type") == "list")
            {
                await Wire.WriteAsync(pipe, this.List(), handshakeTimeout.Token);
            }
            else if (Wire.Text(hello, "type") == "watch" && hello["version"]?.GetValue<int>() is 1 or 2)
            {
                await this.WatchAsync(pipe);
            }
            else if (Wire.Text(hello, "type") == "command")
            {
                await Wire.WriteAsync(pipe, await this.ExecuteAsync(hello), handshakeTimeout.Token);
            }
            else
            {
                throw new InvalidDataException("Unknown client request.");
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or
            OperationCanceledException or ArgumentException or FormatException)
        {
            Console.Error.WriteLine($"Connection ended: {error.Message}");
        }
        finally
        {
            lock (this._gate)
            {
                this._clients--;
                this._lastActivity = Stopwatch.GetTimestamp();
            }
            if (browser is not null)
            {
                lock (this._gate)
                {
                    if (this._browsers.Remove(browser.Id)) this.Changed();
                    foreach (var completion in browser.Pending.Values)
                    {
                        completion.TrySetResult(Wire.Status("session-gone"));
                    }
                }
            }
        }
    }

    private async Task ExpireBrowsersAsync(CancellationTokenSource lifetime, TimeSpan? idleTimeout)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                Browser[] expired;
                lock (this._gate)
                {
                    expired = this._browsers.Values.Where(browser => browser.RequiresHeartbeat &&
                        Stopwatch.GetElapsedTime(browser.LastSeen) >= TimeSpan.FromSeconds(20)).ToArray();
                    foreach (var browser in expired)
                    {
                        this._browsers.Remove(browser.Id);
                        foreach (var completion in browser.Pending.Values) completion.TrySetResult(Wire.Status("session-gone"));
                    }
                    if (expired.Length > 0) this.Changed();
                }

                // Closing the transport forces a fresh connection and bindings after browser recovery.
                foreach (var browser in expired) browser.Disconnect();
                lock (this._gate)
                {
                    if (idleTimeout is { } timeout && this._clients == 0 && Stopwatch.GetElapsedTime(this._lastActivity) >= timeout)
                        lifetime.Cancel();
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private JsonObject List()
    {
        var connections = new JsonArray();
        lock (this._gate)
        {
            foreach (var browser in this._browsers.Values)
            {
                connections.Add((JsonNode)new JsonObject
                {
                    ["connectionId"] = browser.Id,
                    ["epoch"] = browser.Epoch,
                    ["browser"] = browser.Hello["browser"]?.DeepClone(),
                    ["profileId"] = browser.Hello["profileId"]?.DeepClone(),
                    ["applicationId"] = browser.ApplicationId,
                    ["browserExecutable"] = browser.ExecutablePath,
                    ["heartbeatAgeMs"] = browser.HasHeartbeat ? (long)Stopwatch.GetElapsedTime(browser.LastSeen).TotalMilliseconds : -1,
                    ["hasSnapshot"] = browser.Revision > 0 && browser.RequiresHeartbeat,
                    ["revision"] = browser.Revision,
                    ["sessions"] = browser.Sessions.DeepClone(),
                    ["tabs"] = new JsonArray(browser.Tabs.Values.Select(tab => (JsonNode)new JsonObject
                    {
                        ["tabId"] = tab.TabId, ["sessionId"] = tab.SessionId, ["generation"] = tab.Generation
                    }).ToArray())
                });
            }

            return new JsonObject { ["type"] = "catalog", ["version"] = 2, ["revision"] = this._revision, ["connections"] = connections };
        }
    }

    // Called under the catalog lock; watchers read the latest complete snapshot.
    private void Changed()
    {
        this._revision++;
        foreach (var watcher in this._watchers) watcher.Writer.TryWrite(true);
    }

    private async Task WatchAsync(NamedPipeServerStream pipe)
    {
        var changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (this._gate)
        {
            this._watchers.Add(changes);
            changes.Writer.TryWrite(true);
        }

        using var lifetime = new CancellationTokenSource();
        var disconnected = Wire.ReadAsync(pipe, lifetime.Token);
        var changed = changes.Reader.ReadAsync(lifetime.Token).AsTask();
        try
        {
            while (true)
            {
                if (await Task.WhenAny(changed, disconnected, Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token)) == disconnected) break;
                if (changed.IsCompleted)
                {
                    await changed;
                    changed = changes.Reader.ReadAsync(lifetime.Token).AsTask();
                }
                using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                writeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await Wire.WriteAsync(pipe, this.List(), writeTimeout.Token);
            }
        }
        finally
        {
            lock (this._gate) this._watchers.Remove(changes);
            changes.Writer.TryComplete();
            await lifetime.CancelAsync();
            try { await disconnected; }
            catch (OperationCanceledException) { }
            try { await changed; }
            catch (Exception error) when (error is OperationCanceledException or ChannelClosedException) { }
        }
    }

    private async Task<JsonObject> ExecuteAsync(JsonObject request)
    {
        Browser browser;
        var requestId = Guid.NewGuid().ToString();
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = Wire.Text(request, "operation");
        var deadline = Math.Min(request["deadline"]?.GetValue<long>() ?? long.MaxValue,
            DateTimeOffset.UtcNow.AddSeconds(3).ToUnixTimeMilliseconds());
        lock (this._gate)
        {
            if (!this._browsers.TryGetValue(Wire.Text(request, "connectionId"), out browser!))
            {
                return Wire.Status("session-gone");
            }

            var session = browser.Sessions.OfType<JsonObject>().FirstOrDefault(item =>
                Wire.Text(item, "id") == Wire.Text(request, "sessionId"));
            if (session is null)
            {
                return Wire.Status("session-gone");
            }

            if (!MatchesTab(browser, request)) return Wire.Status("session-gone");
            if (deadline <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) return Wire.Status("unavailable");

            if (session["capabilities"] is not JsonArray capabilities ||
                !capabilities.Any(item => item?.GetValue<string>() == operation))
            {
                return Wire.Status("unsupported");
            }

            if (browser.Pending.Count >= 32)
            {
                return Wire.Status("unavailable");
            }

            browser.Pending.Add(requestId, completion);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1, deadline - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));
        try
        {
            var sent = await browser.SendAsync(new JsonObject
            {
                ["type"] = "command", ["requestId"] = requestId, ["epoch"] = browser.Epoch,
                ["sessionId"] = request["sessionId"]?.DeepClone(), ["operation"] = operation,
                ["value"] = request["value"]?.DeepClone(),
                ["deadline"] = deadline,
                ["allowForeground"] = request["allowForeground"]?.GetValue<bool>() == true
            }, timeout.Token, () =>
            {
                lock (this._gate)
                {
                    return this._browsers.ContainsKey(browser.Id) && MatchesTab(browser, request) &&
                        deadline > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() &&
                        browser.Sessions.Any(item => item is JsonObject value && Wire.Text(value, "id") == Wire.Text(request, "sessionId"));
                }
            });
            if (!sent) return Wire.Status("session-gone");
            return await completion.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return Wire.Status("unconfirmed");
        }
        catch (IOException)
        {
            return Wire.Status("session-gone");
        }
        finally
        {
            lock (this._gate)
            {
                browser.Pending.Remove(requestId);
            }
        }
    }

    private static bool MatchesTab(Browser browser, JsonObject request)
    {
        if (request["tabId"] is null && request["generation"] is null) return true;
        return request["tabId"] is JsonValue tab && request["generation"] is JsonValue generation &&
            browser.Tabs.TryGetValue(tab.GetValue<int>(), out var binding) &&
            binding.Generation == generation.GetValue<long>() && binding.SessionId == Wire.Text(request, "sessionId");
    }

    private sealed record Tab(int TabId, string SessionId, long Generation);

    private sealed class Browser(NamedPipeServerStream pipe, JsonObject hello, string applicationId, string executablePath)
    {
        public string ExecutablePath { get; } = executablePath;
        private readonly SemaphoreSlim _writes = new(1);
        private long _nextGeneration;

        internal string Id { get; } = Guid.NewGuid().ToString();
        internal string ApplicationId { get; } = applicationId;
        internal bool HasHeartbeat { get; set; }
        internal string Epoch { get; } = Wire.Text(hello, "epoch");
        internal JsonObject Hello { get; } = hello;
        internal bool RequiresHeartbeat { get; } = hello["heartbeatIntervalMs"]?.GetValue<int>() == 5000;
        internal long LastSeen { get; set; } = Stopwatch.GetTimestamp();
        internal long Revision { get; set; }
        internal JsonArray Sessions { get; set; } = [];
        internal Dictionary<string, TaskCompletionSource<JsonObject>> Pending { get; } = [];
        internal Dictionary<int, Tab> Tabs { get; private set; } = [];

        internal void Disconnect() => pipe.Dispose();

        internal void UpdateTabs()
        {
            var tabs = new Dictionary<int, Tab>();
            foreach (var group in this.Sessions.OfType<JsonObject>().Where(item => item["tabId"] is not null)
                .GroupBy(item => item["tabId"]!.GetValue<int>()))
            {
                var playing = group.Where(item => Wire.Text(item, "state") == "playing")
                    .Select(item => Wire.Text(item, "documentId")).ToHashSet();
                int Rank(JsonObject item)
                {
                    var playback = item["capabilities"] is JsonArray caps && caps.Any(cap => cap?.GetValue<string>() is "play" or "pause");
                    return Wire.Text(item, "kind") switch
                    {
                        "media-session" when playback => playing.Contains(Wire.Text(item, "documentId")) ? 0 : 2,
                        "html-media" when playback => Wire.Text(item, "state") switch { "playing" => 1, "ended" => 4, _ => 3 },
                        _ => 5
                    };
                }

                var rank = group.Min(Rank);
                var candidates = group.Where(item => Rank(item) == rank).ToArray();
                this.Tabs.TryGetValue(group.Key, out var previous);
                var primary = candidates.FirstOrDefault(item => Wire.Text(item, "id") == previous?.SessionId) ??
                    candidates.FirstOrDefault(item => item["frameId"]?.GetValue<int>() == 0) ?? candidates[0];
                var id = Wire.Text(primary, "id");
                tabs.Add(group.Key, previous?.SessionId == id ? previous : new Tab(group.Key, id, ++this._nextGeneration));
            }

            this.Tabs = tabs;
        }

        internal async Task<bool> SendAsync(JsonObject message, CancellationToken cancellationToken = default, Func<bool>? validate = null)
        {
            await this._writes.WaitAsync(cancellationToken);
            try
            {
                if (validate is not null && !validate()) return false;
                if (Wire.Text(message, "type") == "command")
                {
                    var received = message["allowForeground"]?.GetValue<bool>() == true;
                    message["allowForeground"] = Wire.Text(message, "operation") is "play" or "activate" &&
                        message["allowForeground"]?.GetValue<bool>() == true && ForegroundPermission.AllowBridge(pipe);
                    ForegroundPermission.Trace("relay", message, received);
                }
                await Wire.WriteAsync(pipe, message, cancellationToken);
                return true;
            }
            catch
            {
                // A partial write invalidates framing for the entire connection.
                pipe.Dispose();
                throw;
            }
            finally
            {
                this._writes.Release();
            }
        }
    }
}