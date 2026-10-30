// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Browser;

/// <summary>Connects the browser companion to the media service.</summary>
public sealed class BrowserBackend : IMediaBackend
{
    private readonly object _gate = new();
    private readonly string _pipeName;
    private readonly BrowserFallbackPolicy _fallback;
    private readonly TimeProvider _clock;
    private readonly long _startedAt;
    private BrowserFallbackView? _lastView;
    private readonly Func<NamedPipeClientStream, bool> _allowForeground;
    private readonly BrowserImageLoader _images;
    private readonly string? _fallbackIconPath;
    private readonly Dictionary<string, IconEntry> _icons = new(StringComparer.Ordinal);
    private readonly Dictionary<MediaArtworkKey, MediaArtworkContent> _artwork = [];
    private readonly SemaphoreSlim _artworkGate = new(1);
    private readonly Channel<bool> _iconChanges = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<MediaBackendSignal> _signals = Channel.CreateBounded<MediaBackendSignal>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private Dictionary<MediaBackendSessionId, Binding> _bindings = [];
    private MediaBackendSnapshot _snapshot = new(0, [], [], MediaControlAvailability.Unavailable)
    {
        Connection = MediaBackendConnectionState.Connecting
    };
    private Task? _monitor;
    private Task? _iconMonitor;
    private Task? _healthMonitor;
    private Task? _dispose;
    private long _nextId;
    private long _nextGeneration;
    private long _nextArtwork;

    /// <summary>Creates a dormant provider for the current user's companion service.</summary>
    public BrowserBackend() : this(fallbackIconPath: null, imageCacheDirectory: null) { }

    /// <summary>Creates a provider with a host-readable fallback icon and optional image cache root.</summary>
    /// <param name="fallbackIconPath">Generic browser icon used when the tab favicon is unavailable.</param>
    /// <param name="imageCacheDirectory">Optional directory for temporary favicon files.</param>
    public BrowserBackend(string? fallbackIconPath, string? imageCacheDirectory)
        : this(BrowserWire.DefaultPipeName(), new BrowserImageLoader(cacheDirectory: imageCacheDirectory), fallbackIconPath) { }

    internal BrowserBackend(string pipeName, TimeSpan? recoveryDelay = null)
        : this(pipeName, new BrowserImageLoader(), null, recoveryDelay: recoveryDelay) { }

    internal BrowserBackend(string pipeName, BrowserImageLoader images, string? fallbackIconPath,
        Func<NamedPipeClientStream, bool>? allowForeground = null, TimeSpan? recoveryDelay = null, TimeProvider? clock = null)
    {
        this._pipeName = pipeName;
        this._fallback = new(recoveryDelay);
        this._clock = clock ?? TimeProvider.System;
        this._startedAt = this._clock.GetTimestamp();
        this._allowForeground = allowForeground ?? BrowserForegroundPermission.AllowRelay;
        this._images = images;
        this._fallbackIconPath = fallbackIconPath;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this._gate)
        {
            ObjectDisposedException.ThrowIf(this._dispose is not null, this);
            this._monitor ??= Task.Run(this.MonitorAsync, CancellationToken.None);
            this._iconMonitor ??= Task.Run(this.MonitorIconsAsync, CancellationToken.None);
            this._healthMonitor ??= Task.Run(this.MonitorHealthAsync, CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MediaBackendSignal> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this._lifetime.Token);
        await foreach (var signal in this._signals.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false)) yield return signal;
    }

    /// <inheritdoc />
    public Task<MediaBackendSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this._gate) return Task.FromResult(this._snapshot);
    }

    /// <inheritdoc />
    public void InvalidateObservations(ImmutableArray<MediaBackendObservationRequest> requests)
    {
        // The companion pushes complete observations after commands and page events.
    }

    /// <inheritdoc />
    public async ValueTask<MediaArtworkContent?> GetArtworkAsync(MediaArtworkKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this._gate) { if (!this.MatchesArtwork(key, out _)) return null; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this._lifetime.Token);
        await this._artworkGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            string url;
            lock (this._gate)
            {
                if (!this.MatchesArtwork(key, out var binding)) return null;
                if (this._artwork.TryGetValue(key, out var cached)) return cached;
                url = binding.Target.ArtworkUrl;
            }
            var content = await this._images.LoadAsync(url, icon: false, linked.Token).ConfigureAwait(false);
            lock (this._gate)
            {
                if (!this.MatchesArtwork(key, out var binding)) return null;
                if (content is not null)
                {
                    if (this._artwork.Count >= 8) this._artwork.Remove(this._artwork.Keys.First());
                    this._artwork[key] = content;
                }
                else this._bindings[binding.Id] = binding with { RetryArtworkAt = DateTimeOffset.UtcNow.AddSeconds(30) };
                return content;
            }
        }
        finally { this._artworkGate.Release(); }
    }

    private bool MatchesArtwork(MediaArtworkKey key, [NotNullWhen(true)] out Binding? binding)
    {
        binding = null;
        return this._dispose is null && this._bindings.TryGetValue(new(key.SessionId.Value), out binding) && binding.Artwork == key;
    }

    /// <inheritdoc />
    public async Task<MediaBackendCommandResult> ExecuteAsync(MediaBackendCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (operation, capability) = command.Operation switch
        {
            MediaOperation.Play => ("play", MediaCapabilities.Play),
            MediaOperation.Pause => ("pause", MediaCapabilities.Pause),
            MediaOperation.SkipNext => ("nexttrack", MediaCapabilities.SkipNext),
            MediaOperation.SkipPrevious => ("previoustrack", MediaCapabilities.SkipPrevious),
            MediaOperation.ActivateSource => ("activate", MediaCapabilities.ActivateSource),
            _ => (string.Empty, MediaCapabilities.None)
        };
        Binding binding;
        lock (this._gate)
        {
            if (!this.TryGetBinding(command, out binding!)) return new(MediaBackendCommandStatus.SessionGone, null);
            if (capability == MediaCapabilities.None || !binding.Target.Capabilities.HasFlag(capability))
                return new(MediaBackendCommandStatus.Unsupported, null);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this._lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3).ToUnixTimeMilliseconds();
        var sent = false;
        try
        {
            await using var pipe = BrowserWire.CreatePipe(this._pipeName);
            await pipe.ConnectAsync(1500, timeout.Token).ConfigureAwait(false);
            lock (this._gate)
            {
                if (!this.TryGetBinding(command, out var current) || current.Target.SessionId != binding.Target.SessionId)
                    return new(MediaBackendCommandStatus.SessionGone, null);
                if (!current.Target.Capabilities.HasFlag(capability)) return new(MediaBackendCommandStatus.Unsupported, null);
            }

            timeout.Token.ThrowIfCancellationRequested();
            var allowForeground = command.Operation is MediaOperation.Play or MediaOperation.ActivateSource && this._allowForeground(pipe);
            sent = true;
            await BrowserWire.WriteAsync(pipe, writer =>
            {
                writer.WriteString("type", "command");
                writer.WriteString("connectionId", binding.Target.ConnectionId);
                writer.WriteString("sessionId", binding.Target.SessionId);
                writer.WriteNumber("tabId", binding.Target.TabId);
                writer.WriteNumber("generation", binding.Target.Generation);
                writer.WriteString("operation", operation);
                writer.WriteNumber("deadline", deadline);
                writer.WriteBoolean("allowForeground", allowForeground);
            }, timeout.Token).ConfigureAwait(false);
            using var response = await BrowserWire.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
            var status = BrowserWire.Text(response.RootElement, "status") switch
            {
                "completed" => MediaBackendCommandStatus.Completed,
                "unsupported" => MediaBackendCommandStatus.Unsupported,
                "session-gone" => MediaBackendCommandStatus.SessionGone,
                "unavailable" => MediaBackendCommandStatus.Unavailable,
                "unconfirmed" => MediaBackendCommandStatus.Unconfirmed,
                "failed" => MediaBackendCommandStatus.Failed,
                _ => MediaBackendCommandStatus.Unconfirmed
            };
            return new(status, BrowserWire.Text(response.RootElement, "detail"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !this._lifetime.IsCancellationRequested)
        {
            return new(sent ? MediaBackendCommandStatus.Unconfirmed : MediaBackendCommandStatus.Unavailable, "Browser command deadline elapsed.");
        }
        catch (Exception error) when (IsTransportFailure(error))
        {
            return new(sent ? MediaBackendCommandStatus.Unconfirmed : MediaBackendCommandStatus.Unavailable, error.Message);
        }
    }

    private bool TryGetBinding(MediaBackendCommand command, [NotNullWhen(true)] out Binding? binding)
    {
        binding = null;
        return this._dispose is null && this._bindings.TryGetValue(command.SessionId, out binding) && binding.Generation == command.BindingGeneration;
    }

    private async Task MonitorAsync()
    {
        var cancellationToken = this._lifetime.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = BrowserWire.CreatePipe(this._pipeName);
                await pipe.ConnectAsync(1500, cancellationToken).ConfigureAwait(false);
                await BrowserWire.WriteAsync(pipe, writer =>
                {
                    writer.WriteString("type", "watch");
                    writer.WriteNumber("version", 2);
                }, cancellationToken).ConfigureAwait(false);
                var revision = -1L;
                while (true)
                {
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                    using var document = await BrowserWire.ReadAsync(pipe, readTimeout.Token).ConfigureAwait(false);
                    var catalog = BrowserCatalog.Parse(document.RootElement);
                    if (catalog.Revision < revision) throw new InvalidDataException("Browser catalog revision decreased.");
                    revision = catalog.Revision;
                    lock (this._gate)
                        this.Apply(this._fallback.Evaluate(this._clock.GetElapsedTime(this._startedAt), catalog));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception error) when (IsTransportFailure(error) || error is OperationCanceledException)
            {
                this.Disconnect(error.Message);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Apply(BrowserFallbackView view)
    {
        lock (this._gate)
        {
            if (this._dispose is not null) return;
            if (this._lastView is { } previousView && view.HasSameContent(previousView) &&
                !this._bindings.Values.Any(binding => binding.RetryArtworkAt <= DateTimeOffset.UtcNow)) return;
            this._lastView = view;
            var previous = this._bindings.Values.ToDictionary(binding => (binding.Target.ConnectionId, binding.Target.TabId));
            var bindings = new Dictionary<MediaBackendSessionId, Binding>();
            var sessions = ImmutableArray.CreateBuilder<MediaBackendSessionSnapshot>();
            foreach (var target in view.Targets)
            {
                previous.TryGetValue((target.ConnectionId, target.TabId), out var old);
                var id = old?.Id ?? new MediaBackendSessionId(++this._nextId);
                var generation = old is not null && old.Target.Generation == target.Generation && old.Target.SessionId == target.SessionId
                    ? old.Generation : ++this._nextGeneration;
                var sameArtwork = old is not null && old.Generation == generation && old.Target.ArtworkUrl == target.ArtworkUrl &&
                    old.Target.Properties.Title == target.Properties.Title && old.Target.Properties.Artist == target.Properties.Artist &&
                    old.Target.Properties.AlbumTitle == target.Properties.AlbumTitle && !(old.RetryArtworkAt <= DateTimeOffset.UtcNow);
                MediaArtworkKey? artwork = target.ArtworkUrl.Length == 0 ? null : sameArtwork ? old!.Artwork : new(new(id.Value), ++this._nextArtwork);
                bindings.Add(id, new(id, generation, target, artwork, sameArtwork ? old!.RetryArtworkAt : null));
                var properties = target.Properties with
                {
                    Artwork = artwork,
                    Source = target.Properties.Source with { IconPath = this.IconPath(target) }
                };
                sessions.Add(new(id, generation, properties, target.Timeline, target.State, target.Capabilities)
                {
                    Origin = new(target.Origin)
                });
            }

            this._bindings = bindings;
            foreach (var key in this._artwork.Keys.Where(key => !this.MatchesArtwork(key, out _)).ToArray()) this._artwork.Remove(key);
            this._snapshot = new(this._snapshot.Revision + 1, sessions.ToImmutable(),
                [.. sessions.Where(session => session.PlaybackState == MediaPlaybackState.Playing).Select(session => session.Id)],
                view.Connection.Status == MediaConnectionStatus.Connected ? MediaControlAvailability.Available : MediaControlAvailability.Unavailable)
            {
                Connection = view.Connection,
                ReplacesSources = view.Claims
            };
            this.Signal();
            this._iconChanges.Writer.TryWrite(true);
        }
    }

    private void Disconnect(string? diagnostic)
    {
        lock (this._gate)
        {
            if (this._dispose is null)
            {
                var view = this._fallback.Evaluate(this._clock.GetElapsedTime(this._startedAt), disconnected: true);
                this.Apply(view);
                return;
            }
            this._bindings.Clear();
            this._artwork.Clear();
            this._snapshot = new(this._snapshot.Revision + 1, [], [], MediaControlAvailability.Unavailable)
            {
                Connection = new(MediaConnectionStatus.Disconnected, diagnostic),
                ReplacesSources = []
            };
            this.Signal();
        }
    }

    private void Signal() => this._signals.Writer.TryWrite(MediaBackendSignal.ObservationsChanged | MediaBackendSignal.SessionsChanged |
        MediaBackendSignal.CurrentSessionChanged | MediaBackendSignal.BackendsChanged);

    private async Task MonitorHealthAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), this._clock);
        try
        {
            while (await timer.WaitForNextTickAsync(this._lifetime.Token).ConfigureAwait(false))
            {
                lock (this._gate) this.Apply(this._fallback.Evaluate(this._clock.GetElapsedTime(this._startedAt)));
            }
        }
        catch (OperationCanceledException) when (this._lifetime.IsCancellationRequested) { }
    }

    private static bool IsTransportFailure(Exception error) => error is IOException or InvalidDataException or TimeoutException or JsonException or
        InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException or OverflowException;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (this._gate) return new(this._dispose ??= Task.Run(this.DisposeCoreAsync, CancellationToken.None));
    }

    private async Task DisposeCoreAsync()
    {
        await this._lifetime.CancelAsync().ConfigureAwait(false);
        if (this._monitor is not null) await this._monitor.ConfigureAwait(false);
        if (this._iconMonitor is not null) await this._iconMonitor.ConfigureAwait(false);
        if (this._healthMonitor is not null) await this._healthMonitor.ConfigureAwait(false);
        await this._artworkGate.WaitAsync().ConfigureAwait(false);
        this._images.Dispose();
        this._artworkGate.Release();
        this.Disconnect(null);
        this._signals.Writer.TryComplete();
        this._lifetime.Dispose();
    }

    private string? IconPath(BrowserTarget target)
    {
        if (this._icons.TryGetValue(target.FaviconUrl, out var icon) && icon.Path is not null) return icon.Path;
        return this._fallbackIconPath;
    }

    private async Task MonitorIconsAsync()
    {
        var token = this._lifetime.Token;
        try
        {
            await foreach (var change in this._iconChanges.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                string[] urls;
                lock (this._gate)
                {
                    urls = this._bindings.Values.Select(binding => binding.Target.FaviconUrl).Where(url => url.Length > 0).Distinct().Take(64)
                        .Where(url => !this._icons.TryGetValue(url, out var icon) || (icon.Path is null && icon.RetryAt <= DateTimeOffset.UtcNow)).ToArray();
                }
                foreach (var url in urls)
                {
                    var path = await this._images.LoadIconAsync(url, token).ConfigureAwait(false);
                    lock (this._gate)
                    {
                        if (this._icons.Count >= 64 && !this._icons.ContainsKey(url)) this._icons.Remove(this._icons.Keys.First());
                        this._icons[url] = new(path, DateTimeOffset.UtcNow.AddMinutes(1));
                        var sessions = this._snapshot.Sessions.Select(session => this._bindings.TryGetValue(session.Id, out var binding)
                            ? session with { MediaProperties = session.MediaProperties with
                            {
                                Source = session.MediaProperties.Source with { IconPath = this.IconPath(binding.Target) }
                            } } : session).ToImmutableArray();
                        this._snapshot = this._snapshot with { Revision = this._snapshot.Revision + 1, Sessions = sessions };
                        this.Signal();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private sealed record IconEntry(string? Path, DateTimeOffset RetryAt);
    private sealed record Binding(MediaBackendSessionId Id, long Generation, BrowserTarget Target, MediaArtworkKey? Artwork, DateTimeOffset? RetryArtworkAt);
}