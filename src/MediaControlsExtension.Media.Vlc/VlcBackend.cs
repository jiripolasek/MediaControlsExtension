// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Vlc;

/// <summary>Polls one local or remote VLC 3 HTTP interface and exposes its player as one session.</summary>
/// <remarks>Observed disconnects and settings changes retire bindings. HTTP has no server instance token or atomic command preconditions.</remarks>
public sealed class VlcBackend : IMediaBackend
{
    private const int MaxArtworkBytes = 32 * 1024 * 1024;
    private static readonly TimeSpan ArtworkRetryDelay = TimeSpan.FromSeconds(10);
    private const MediaBackendSignal Changed = MediaBackendSignal.ObservationsChanged |
                                               MediaBackendSignal.SessionsChanged |
                                               MediaBackendSignal.BackendsChanged;
    private readonly Func<VlcConnectionOptions> _getOptions;
    private readonly Func<HttpMessageHandler> _createHandler;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly string? _sourceIconPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _artworkGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _lifecycleLock = new();
    private readonly Channel<MediaBackendSignal> _signals = Channel.CreateBounded<MediaBackendSignal>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private MediaBackendSnapshot _snapshot = new(0, [], [], MediaControlAvailability.Unavailable)
    {
        Connection = MediaBackendConnectionState.Connecting,
    };
    private VlcConnectionOptions? _options;
    private HttpClient? _client;
    private Task? _monitor;
    private Task? _disposeTask;
    private long _generation;
    private string? _repeatCommand;
    private VlcArtworkReference? _artworkReference;
    private MediaArtworkKey? _artworkKey;
    private MediaArtworkContent? _artworkContent;
    private DateTimeOffset? _artworkRetryAt;
    private long _nextArtworkVersion;

    /// <summary>Creates a dormant provider; reads current immutable options at each observation and command.</summary>
    /// <param name="getOptions">Thread-safe options reader; settings changes apply without disabling the provider.</param>
    /// <param name="sourceIconPath">Optional host-readable provider icon, independent of a local VLC installation.</param>
    public VlcBackend(Func<VlcConnectionOptions> getOptions, string? sourceIconPath = null)
        : this(getOptions, static () => new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false },
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), sourceIconPath: sourceIconPath)
    {
    }

    internal VlcBackend(Func<VlcConnectionOptions> getOptions, Func<HttpMessageHandler> createHandler,
        TimeSpan pollInterval, TimeSpan requestTimeout, TimeProvider? timeProvider = null, string? sourceIconPath = null)
    {
        ArgumentNullException.ThrowIfNull(getOptions);
        ArgumentNullException.ThrowIfNull(createHandler);
        this._getOptions = getOptions;
        this._createHandler = createHandler;
        this._pollInterval = pollInterval;
        this._requestTimeout = requestTimeout;
        this._timeProvider = timeProvider ?? TimeProvider.System;
        this._sourceIconPath = sourceIconPath;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this._lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(this._disposeTask is not null, this);
            if (this._monitor is not null)
            {
                throw new InvalidOperationException("The VLC provider has already started.");
            }

            this._monitor = this.NotifyPeriodicallyAsync(this._lifetime.Token);
            this.Signal();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MediaBackendSignal> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this._lifetime.Token);
        await foreach (var signal in this._signals.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
        {
            yield return signal;
        }
    }

    /// <inheritdoc />
    public async Task<MediaBackendSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this._lifetime.Token);
        await this._gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            return await this.RefreshAsync(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            this._gate.Release();
        }
    }

    /// <inheritdoc />
    public void InvalidateObservations(ImmutableArray<MediaBackendObservationRequest> requests)
    {
        if (requests.Any(static request => request.SessionId.Value == 1 && request.Changes != MediaBackendObservationChanges.None))
        {
            this.Signal();
        }
    }

    /// <inheritdoc />
    public async Task<MediaBackendCommandResult> ExecuteAsync(MediaBackendCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this._lifetime.Token);
        await this._gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (!this.Matches(command))
            {
                return new(MediaBackendCommandStatus.SessionGone, null);
            }

            await this.RefreshAsync(linked.Token).ConfigureAwait(false);
            if (this._getOptions() == this._options && this._snapshot.Connection.Status == MediaConnectionStatus.Disconnected)
            {
                return new(MediaBackendCommandStatus.Unavailable, this._snapshot.Connection.DiagnosticMessage);
            }

            if (!this.Matches(command) || this._getOptions() != this._options)
            {
                return new(MediaBackendCommandStatus.SessionGone, this._snapshot.Connection.DiagnosticMessage);
            }

            var state = this._snapshot.Sessions[0].PlaybackState;
            var operation = command.Operation switch
            {
                MediaOperation.Play when state == MediaPlaybackState.Playing => string.Empty,
                MediaOperation.Play when state == MediaPlaybackState.Paused => "pl_forceresume",
                MediaOperation.Play => "pl_play",
                MediaOperation.Pause => "pl_forcepause",
                MediaOperation.Stop => "pl_stop",
                MediaOperation.SkipNext => "pl_next",
                MediaOperation.SkipPrevious => "pl_previous",
                MediaOperation.ToggleShuffle when (this._snapshot.Sessions[0].Capabilities & MediaCapabilities.ToggleShuffle) != 0 =>
                    "pl_random",
                MediaOperation.ToggleRepeat => this._repeatCommand,
                _ => null,
            };
            if (operation is null)
            {
                return new(MediaBackendCommandStatus.Unsupported, null);
            }

            if (operation.Length != 0)
            {
                using var response = await this.GetJsonAsync(
                    $"requests/status.json?command={operation}", linked.Token, isCommand: true).ConfigureAwait(false);
                // VLC may report the old state while an accepted command is still taking effect.
                _ = VlcObservation.ParseStatus(response.RootElement);
            }

            return new(MediaBackendCommandStatus.Completed, null);
        }
        catch (Exception ex) when (IsTransportFailure(ex, linked.Token))
        {
            this.Disconnect(Diagnostic(ex));
            return new(MediaBackendCommandStatus.Unavailable, this._snapshot.Connection.DiagnosticMessage);
        }
        catch (OperationCanceledException)
        {
            this.Disconnect(Resources.Strings.Connection_RequestCanceled);
            throw;
        }
        finally
        {
            this.Signal();
            this._gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<MediaArtworkContent?> GetArtworkAsync(MediaArtworkKey key, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this._lifetime.Token);
        await this._artworkGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            VlcArtworkReference reference;
            VlcConnectionOptions options;
            await this._gate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                if (!this.MatchesArtwork(key) || this._artworkReference is null)
                {
                    return null;
                }

                if (this._artworkContent is not null || this._artworkRetryAt is not null)
                {
                    return this._artworkContent;
                }

                reference = this._artworkReference;
                options = this._options!;
            }
            finally
            {
                this._gate.Release();
            }

            // Artwork has its own client so a slow image cannot occupy the control lane.
            var content = await this.ReadArtworkContentAsync(options, reference, linked.Token).ConfigureAwait(false);
            await this._gate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                if (!this.MatchesArtwork(key))
                {
                    return null;
                }

                if (content is not null)
                {
                    await this.RefreshAsync(linked.Token).ConfigureAwait(false);
                    this.Signal();
                    if (!this.MatchesArtwork(key))
                    {
                        return null;
                    }
                }

                this._artworkContent = content;
                this._artworkRetryAt = content is null ? this._timeProvider.GetUtcNow() + ArtworkRetryDelay : null;
                return content;
            }
            finally
            {
                this._gate.Release();
            }
        }
        finally
        {
            this._artworkGate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (this._lifecycleLock)
        {
            return new(this._disposeTask ??= this.DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await this._lifetime.CancelAsync().ConfigureAwait(false);
        if (this._monitor is not null)
        {
            await this._monitor.ConfigureAwait(false);
        }

        await this._artworkGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await this._gate.WaitAsync().ConfigureAwait(false);
            try
            {
                this._client?.Dispose();
                this._client = null;
                this.UpdateArtwork(null);
                this._signals.Writer.TryComplete();
            }
            finally
            {
                this._gate.Release();
            }
        }
        finally
        {
            this._artworkGate.Release();
        }

        this._lifetime.Dispose();
        this._gate.Dispose();
        this._artworkGate.Dispose();
    }

    private async Task NotifyPeriodicallyAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(this._pollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                this.Signal();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void Signal() => this._signals.Writer.TryWrite(Changed);

    private bool Matches(MediaBackendCommand command) => this._snapshot.Sessions is [var session] &&
        session.Id == command.SessionId && session.BindingGeneration == command.BindingGeneration;

    private bool MatchesArtwork(MediaArtworkKey key) => this._artworkKey == key && this._options == this._getOptions() &&
        this._snapshot.Sessions is [var session] && session.MediaProperties.Artwork == key;

    private void UpdateArtwork(VlcArtworkReference? reference)
    {
        if (reference == this._artworkReference &&
            (this._artworkRetryAt is null || this._timeProvider.GetUtcNow() < this._artworkRetryAt))
        {
            return;
        }

        this._artworkReference = reference;
        this._artworkKey = reference is null ? null : new(new(1), ++this._nextArtworkVersion);
        this._artworkContent = null;
        this._artworkRetryAt = null;
    }

    private async Task<MediaBackendSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!this.ConfigureClient())
            {
                return this._snapshot;
            }

            using var status = await this.GetJsonAsync("requests/status.json", cancellationToken).ConfigureAwait(false);
            var observation = VlcObservation.ParseStatus(status.RootElement);
            if (!observation.HasMedia)
            {
                using var playlist = await this.GetJsonAsync("requests/playlist.json", cancellationToken).ConfigureAwait(false);
                observation = observation.WithPlaylist(playlist.RootElement);
            }

            if (this._getOptions() != this._options)
            {
                this.Disconnect(Resources.Strings.Connection_SettingsChanged);
                return this._snapshot;
            }

            ImmutableArray<MediaBackendSessionSnapshot> sessions = [];
            this.UpdateArtwork(observation.Artwork);
            this._repeatCommand = observation.RepeatCommand;
            if (observation.HasMedia)
            {
                if (this._snapshot.Sessions.IsEmpty)
                {
                    this._generation++;
                }

                var source = observation.Properties.Source with
                {
                    IconPath = this._sourceIconPath,
                    NativeApplication = this._options!.IsLocalConnection ? new("vlc.exe") : null,
                };
                sessions = [new(new(1), this._generation, observation.Properties with { Artwork = this._artworkKey, Source = source },
                    observation.Timeline, observation.Playback, observation.Capabilities)
                {
                    Origin = new(this._options.BaseAddress!.AbsoluteUri, this._options.TreatAsLocal),
                }];
            }

            this._snapshot = new(this._snapshot.Revision + 1, sessions, sessions.IsEmpty ? [] : [new(1)], MediaControlAvailability.Available)
            {
                Connection = this.ConnectionState(MediaConnectionStatus.Connected),
            };
        }
        catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
        {
            this.Disconnect(Diagnostic(ex));
        }
        catch (OperationCanceledException)
        {
            this.Disconnect(Resources.Strings.Connection_ObservationCanceled);
            throw;
        }

        return this._snapshot;
    }

    private bool ConfigureClient()
    {
        var options = this._getOptions();
        if (options != this._options)
        {
            this._client?.Dispose();
            this._client = null;
            this._options = options;
            this.Disconnect(Resources.Strings.Connection_SettingsChanged);
        }

        if (options.BaseAddress is null)
        {
            this.Disconnect(Resources.Strings.Connection_InvalidEndpoint);
            return false;
        }

        if (string.IsNullOrEmpty(options.Password))
        {
            this.Disconnect(Resources.Strings.Connection_PasswordRequired);
            return false;
        }

        if (this._client is null)
        {
            this._client = this.CreateClient(options);
        }

        return true;
    }

    private HttpClient CreateClient(VlcConnectionOptions options)
    {
        var client = new HttpClient(this._createHandler())
        {
            BaseAddress = options.BaseAddress!,
            Timeout = this._requestTimeout,
            MaxResponseContentBufferSize = 1024 * 1024,
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($":{options.Password}")));
        return client;
    }

    private async Task<MediaArtworkContent?> ReadArtworkContentAsync(
        VlcConnectionOptions options, VlcArtworkReference reference, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(this._requestTimeout);
        try
        {
            using var client = this.CreateClient(options);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"art?item={reference.ItemId}")
            {
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is 0 or > MaxArtworkBytes)
            {
                return null;
            }

            await response.Content.LoadIntoBufferAsync(MaxArtworkBytes, timeout.Token).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var type = DetectArtworkContentType(bytes);
            return type is null ? null : new(type, bytes, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
        {
            return null;
        }
    }

    private static string? DetectArtworkContentType(ReadOnlySpan<byte> bytes)
    {
        if (bytes is [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, ..]) return "image/png";
        if (bytes is [0xff, 0xd8, 0xff, ..]) return "image/jpeg";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return "image/gif";
        if (bytes.StartsWith("BM"u8)) return "image/bmp";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken, bool isCommand = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path) { VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        if (isCommand)
        {
            // An explicit empty body prevents .NET from retrying a command after a lost HTTP/1.1 response.
            request.Content = new ByteArrayContent([]);
            request.Headers.ExpectContinue = false;
        }

        using var response = await this._client!.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return JsonDocument.Parse(bytes);
    }

    private void Disconnect(string diagnostic)
    {
        this.UpdateArtwork(null);
        this._repeatCommand = null;
        this._snapshot = new(this._snapshot.Revision + 1, [], [], MediaControlAvailability.Unavailable)
        {
            Connection = this.ConnectionState(MediaConnectionStatus.Disconnected, diagnostic),
        };
    }

    private MediaBackendConnectionState ConnectionState(MediaConnectionStatus status, string? diagnostic = null) => new(status, diagnostic)
    {
        Connections = this._options?.BaseAddress is { } endpoint
            ? [new(endpoint.AbsoluteUri, endpoint.AbsoluteUri, status)] : [],
    };

    private static bool IsTransportFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or JsonException or InvalidDataException or IOException ||
        (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private static string Diagnostic(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } =>
            Resources.Strings.Connection_PasswordRejected,
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } =>
            Resources.Strings.Connection_AccessDenied,
        InvalidDataException => exception.Message,
        JsonException => Resources.Strings.Connection_InvalidJson,
        OperationCanceledException => Resources.Strings.Connection_Timeout,
        _ => Resources.Strings.Connection_Unreachable,
    };
}