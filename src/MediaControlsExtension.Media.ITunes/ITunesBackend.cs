// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.ITunes.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.System;

namespace JPSoftworks.MediaControlsExtension.Media.ITunes;

/// <summary>
/// Controls desktop iTunes through its COM automation interface and manages a dedicated DispatcherQueue message pump thread.
/// </summary>
public sealed class ITunesBackend : IMediaBackend
{
    private static readonly MediaBackendSessionId DefaultSessionId = new(1);
    private static readonly TimeSpan DiscoveryPollInterval = TimeSpan.FromSeconds(2);
    private const string DirectInstallerApplicationId = "Apple.iTunes";
    private const string StoreApplicationId = "AppleInc.iTunes_nzyj5cx40ttqa!iTunes";

    private readonly ILogger _logger;
    private readonly string? _sourceIconPath;
    private readonly Func<string, bool>? _isProcessRunning;
    private readonly Channel<bool> _signals;
    private readonly Lock _stateLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ITunesRefreshScheduler _refreshScheduler = new();

    // Dispatcher-thread-only fields:
    private DispatcherQueueController? _dispatcherController;
    private DispatcherQueue? _dispatcherQueue;
    private DispatcherQueueTimer? _pollTimer;
    private nint _iTunesApp;
    private nint _connectionPoint;
    private uint _adviseCookie;
    private ITunesEventSink? _eventSink;
    private ITunesProcessExitSubscription? _processExitSubscription;
    private bool _isDiscoveryTimerRunning;
    private int? _quittingProcessId;

    // State observed across threads (must be read and written under _stateLock):
    private bool _isConnected;
    private long _bindingGeneration = 1;
    private long _revision = 1;
    private long _artworkVersion = 1;
    private int _cachedArtworkTrackId = -1;
    private MediaArtworkContent? _cachedArtwork;
    private MediaPropertiesSnapshot? _currentMediaProperties;
    private MediaTimelinePropertiesSnapshot _currentTimeline = MediaTimelinePropertiesSnapshot.Empty;
    private MediaPlaybackState _currentPlaybackState = MediaPlaybackState.Stopped;
    private MediaCapabilities _currentCapabilities = MediaCapabilities.None;
    private string? _executablePath;

    // Atomic flags:
    private int _disposeState;
    private int _startState;
    private int _pendingSignals;
    private int _quitDisconnectPending;

    /// <summary>
    /// Initializes a new instance of the <see cref="ITunesBackend"/> class.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostic events.</param>
    /// <param name="sourceIconPath">Optional host-readable icon path override.</param>
    /// <param name="isProcessRunning">Optional delegate to check if the iTunes process is running (useful for deterministic tests).</param>
    public ITunesBackend(
        ILogger<ITunesBackend>? logger = null,
        string? sourceIconPath = null,
        Func<string, bool>? isProcessRunning = null)
    {
        this._logger = logger ?? NullLogger<ITunesBackend>.Instance;
        this._sourceIconPath = sourceIconPath;
        this._isProcessRunning = isProcessRunning;
        this._signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false,
        });
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this._disposeState) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.CompareExchange(ref this._startState, 1, 0) != 0)
        {
            throw new InvalidOperationException("The iTunes backend has already been started.");
        }

        this._dispatcherController = DispatcherQueueController.CreateOnDedicatedThread();
        this._dispatcherQueue = this._dispatcherController.DispatcherQueue;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var ctr = cancellationToken.CanBeCanceled
            ? cancellationToken.UnsafeRegister(static state => ((TaskCompletionSource)state!).TrySetCanceled(), tcs)
            : default;

        var enqueued = this._dispatcherQueue.TryEnqueue(() =>
        {
            ctr.Dispose();

            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                this.ResolveExecutablePath();
                this._pollTimer = this._dispatcherQueue.CreateTimer();
                this._pollTimer.Interval = DiscoveryPollInterval;
                this._pollTimer.IsRepeating = true;
                this._pollTimer.Tick += (_, _) => this.DiscoverITunes();
                this.StartDiscoveryPolling();

                this.DiscoverITunes();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        if (!enqueued)
        {
            ctr.Dispose();
            tcs.TrySetException(new InvalidOperationException("Failed to enqueue initialization onto DispatcherQueue thread."));
        }

        this.SignalChanged(MediaBackendSignal.ObservationsChanged | MediaBackendSignal.SessionsChanged | MediaBackendSignal.BackendsChanged);
        return tcs.Task;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MediaBackendSignal> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this._disposeState) != 0, this);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this._lifetime.Token);
        await foreach (var _ in this._signals.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
        {
            var pending = Interlocked.Exchange(ref this._pendingSignals, 0);
            if (pending != 0)
            {
                yield return (MediaBackendSignal)pending;
            }
        }
    }

    /// <inheritdoc />
    public Task<MediaBackendSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this._disposeState) != 0, this);

        lock (this._stateLock)
        {
            return Task.FromResult(CreateSnapshot(
                this._isConnected,
                this._revision,
                this._bindingGeneration,
                this._currentMediaProperties,
                this._currentTimeline,
                this._currentPlaybackState,
                this._currentCapabilities));
        }
    }

    internal static MediaBackendSnapshot CreateSnapshot(
        bool isConnected,
        long revision,
        long bindingGeneration,
        MediaPropertiesSnapshot? mediaProperties,
        MediaTimelinePropertiesSnapshot timeline,
        MediaPlaybackState playbackState,
        MediaCapabilities capabilities)
    {
        if (!isConnected || mediaProperties == null)
        {
            return new MediaBackendSnapshot(
                revision,
                ImmutableArray<MediaBackendSessionSnapshot>.Empty,
                ImmutableArray<MediaBackendSessionId>.Empty,
                MediaControlAvailability.Available)
            {
                Connection = isConnected ? MediaBackendConnectionState.Connected : MediaBackendConnectionState.Disconnected,
            };
        }

        var session = new MediaBackendSessionSnapshot(
            DefaultSessionId,
            bindingGeneration,
            mediaProperties,
            timeline,
            playbackState,
            capabilities,
            IsAvailable: true)
        {
            Origin = MediaSessionOrigin.Local,
        };

        ImmutableArray<MediaBackendSessionId> hints = playbackState == MediaPlaybackState.Playing
            ? [DefaultSessionId]
            : [];

        return new MediaBackendSnapshot(
            revision,
            [session],
            hints,
            MediaControlAvailability.Available)
        {
            Connection = MediaBackendConnectionState.Connected,
        };
    }

    /// <inheritdoc />
    public Task<MediaBackendCommandResult> ExecuteAsync(MediaBackendCommand command, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this._disposeState) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        lock (this._stateLock)
        {
            if (command.SessionId != DefaultSessionId || command.BindingGeneration != this._bindingGeneration)
            {
                return Task.FromResult(new MediaBackendCommandResult(
                    MediaBackendCommandStatus.SessionGone,
                    "iTunes session is no longer active."));
            }

            if (!this._isConnected || this._dispatcherQueue == null || this.IsDisconnectRequested)
            {
                return Task.FromResult(new MediaBackendCommandResult(
                    MediaBackendCommandStatus.Unavailable,
                    "iTunes is not connected."));
            }
        }

        var tcs = new TaskCompletionSource<MediaBackendCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var ctr = cancellationToken.CanBeCanceled
            ? cancellationToken.UnsafeRegister(static state => ((TaskCompletionSource<MediaBackendCommandResult>)state!).TrySetCanceled(), tcs)
            : default;

        var enqueued = this._dispatcherQueue.TryEnqueue(() =>
        {
            ctr.Dispose();

            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                if (Volatile.Read(ref this._disposeState) != 0)
                {
                    tcs.TrySetResult(new(MediaBackendCommandStatus.Unavailable, "iTunes backend is disposed."));
                    return;
                }

                lock (this._stateLock)
                {
                    if (command.SessionId != DefaultSessionId || command.BindingGeneration != this._bindingGeneration)
                    {
                        tcs.TrySetResult(new(MediaBackendCommandStatus.SessionGone, null));
                        return;
                    }

                    if (!this._isConnected || this._iTunesApp == 0 || this.IsDisconnectRequested)
                    {
                        tcs.TrySetResult(new(MediaBackendCommandStatus.Unavailable, "iTunes is not connected."));
                        return;
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    return;
                }

                var hr = command.Operation switch
                {
                    MediaOperation.Play => ITunesNative.Play(this.GetComCallTarget(this._iTunesApp)),
                    MediaOperation.Pause => ITunesNative.Pause(this.GetComCallTarget(this._iTunesApp)),
                    MediaOperation.Stop => ITunesNative.Stop(this.GetComCallTarget(this._iTunesApp)),
                    MediaOperation.SkipNext => ITunesNative.NextTrack(this.GetComCallTarget(this._iTunesApp)),
                    MediaOperation.SkipPrevious => ITunesNative.BackTrack(this.GetComCallTarget(this._iTunesApp)),
                    MediaOperation.ToggleShuffle => this.ExecuteToggleShuffle(),
                    MediaOperation.ToggleRepeat => this.ExecuteToggleRepeat(),
                    _ => int.MinValue,
                };

                if (hr == int.MinValue)
                {
                    tcs.TrySetResult(new(MediaBackendCommandStatus.Unsupported, $"Operation {command.Operation} is not supported by iTunes."));
                    return;
                }

                this.ThrowIfDisconnectRequested();
                this.UpdatePlaybackAndMetadata();
                this.ThrowIfDisconnectRequested();

                if (hr >= 0)
                {
                    tcs.TrySetResult(new(MediaBackendCommandStatus.Completed, null));
                }
                else
                {
                    tcs.TrySetResult(new(MediaBackendCommandStatus.Failed, $"iTunes COM call returned 0x{hr:X8}"));
                }
            }
            catch (OperationCanceledException) when (this.IsDisconnectRequested)
            {
                tcs.TrySetResult(new(MediaBackendCommandStatus.Unavailable, "iTunes is disconnecting."));
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(new(MediaBackendCommandStatus.Failed, ex.Message));
            }
        });

        if (!enqueued)
        {
            ctr.Dispose();
            return Task.FromResult(new MediaBackendCommandResult(
                MediaBackendCommandStatus.Unavailable,
                "Failed to enqueue command onto iTunes dispatcher thread."));
        }

        return tcs.Task;
    }

    /// <inheritdoc />
    public void InvalidateObservations(ImmutableArray<MediaBackendObservationRequest> requests)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this._disposeState) != 0, this);

        lock (this._stateLock)
        {
            if (!this._isConnected || this._dispatcherQueue == null)
            {
                return;
            }

            var needsPoll = false;
            foreach (var request in requests)
            {
                if (request.SessionId == DefaultSessionId)
                {
                    needsPoll = true;
                    break;
                }
            }

            if (needsPoll)
            {
                this.RequestStateRefresh();
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<MediaArtworkContent?> GetArtworkAsync(MediaArtworkKey key, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this._disposeState) != 0, this);

        lock (this._stateLock)
        {
            if (key.SessionId.Value == DefaultSessionId.Value &&
                key.Version == this._artworkVersion &&
                this._cachedArtwork != null)
            {
                return ValueTask.FromResult<MediaArtworkContent?>(this._cachedArtwork);
            }
        }

        return ValueTask.FromResult<MediaArtworkContent?>(null);
    }

    private void DiscoverITunes()
    {
        if (this.IsDisconnectRequested || this._quittingProcessId != null)
        {
            return;
        }

        lock (this._stateLock)
        {
            if (this._isConnected)
            {
                return;
            }
        }

        var process = this.FindITunesProcess();
        if (process == null)
        {
            return;
        }

        this.Connect(process);
    }

    private Process? FindITunesProcess()
    {
        if (this._isProcessRunning != null && !this._isProcessRunning("iTunes"))
        {
            return null;
        }

        Process[] processes = [];
        Process? selected = null;
        try
        {
            processes = Process.GetProcessesByName("iTunes");
            foreach (var process in processes)
            {
                if (!process.HasExited)
                {
                    this.RecordExecutablePath(process);
                    selected = process;
                    break;
                }
            }
        }
        catch
        {
        }
        finally
        {
            foreach (var process in processes)
            {
                if (!ReferenceEquals(process, selected))
                {
                    process.Dispose();
                }
            }
        }

        return selected;
    }

    private void StartDiscoveryPolling()
    {
        if (this._pollTimer != null && !this._isDiscoveryTimerRunning)
        {
            this._pollTimer.Start();
            this._isDiscoveryTimerRunning = true;
        }
    }

    private void StopDiscoveryPolling()
    {
        if (this._pollTimer != null && this._isDiscoveryTimerRunning)
        {
            this._pollTimer.Stop();
            this._isDiscoveryTimerRunning = false;
        }
    }

    private void Connect(Process process)
    {
        var processAttached = false;
        try
        {
            var hr = ITunesNative.CoCreateInstance(
                ITunesGuids.ClsidiTunesApp,
                0,
                ITunesNative.ClsCtxLocalServer,
                ITunesGuids.IidIiTunes,
                out this._iTunesApp);

            if (hr < 0 || this._iTunesApp == 0)
            {
                lock (this._stateLock)
                {
                    this._isConnected = false;
                }

                return;
            }

            this.HookEvents();
            this.TrackConnectedProcess(process);
            processAttached = true;

            lock (this._stateLock)
            {
                this._isConnected = true;
                this._bindingGeneration++;
                this._revision++;
            }

            this.StopDiscoveryPolling();
            this.SignalChanged(MediaBackendSignal.ObservationsChanged | MediaBackendSignal.SessionsChanged | MediaBackendSignal.CurrentSessionChanged | MediaBackendSignal.BackendsChanged);
            this.UpdatePlaybackAndMetadata();
        }
        catch (Exception ex)
        {
            ITunesLog.ITunesConnectFailed(this._logger, ex);
            this.Disconnect();
        }
        finally
        {
            if (!processAttached)
            {
                process.Dispose();
            }
        }
    }

    private void HookEvents()
    {
        try
        {
            var hr = ITunesNative.QueryInterface(
                this._iTunesApp,
                ITunesGuids.IidIConnectionPointContainer,
                out var pCpc);

            if (hr < 0 || pCpc == 0)
            {
                return;
            }

            try
            {
                hr = ITunesNative.FindConnectionPoint(
                    pCpc,
                    ITunesGuids.DiidIiTunesEvents,
                    out this._connectionPoint);

                if (hr < 0 || this._connectionPoint == 0)
                {
                    return;
                }

                var sink = new ITunesEventSink(this.OnITunesEvent);
                hr = ITunesNative.Advise(
                    this._connectionPoint,
                    sink.IUnknownPointer,
                    out var cookie);

                if (hr >= 0 && cookie != 0)
                {
                    this._eventSink = sink;
                    this._adviseCookie = cookie;
                }
                else
                {
                    sink.Dispose();
                }
            }
            finally
            {
                _ = ITunesNative.Release(pCpc);
            }
        }
        catch (Exception ex)
        {
            ITunesLog.ITunesHookEventsFailed(this._logger, ex);
        }
    }

    private void OnITunesEvent(int dispId)
    {
        try
        {
            if (Volatile.Read(ref this._disposeState) != 0)
            {
                return;
            }

            switch (ITunesEventClassifier.Classify(dispId))
            {
                case ITunesEventAction.Disconnect:
                    this.RequestQuitDisconnect();
                    break;
                case ITunesEventAction.Refresh:
                    this.RequestStateRefresh();
                    break;
            }
        }
        catch
        {
            // COM callbacks must never propagate managed failures back to iTunes.
        }
    }

    private void RequestQuitDisconnect()
    {
        if (Interlocked.CompareExchange(ref this._quitDisconnectPending, 1, 0) != 0)
        {
            return;
        }

        if (this._dispatcherQueue?.TryEnqueue(this.DisconnectForQuit) != true)
        {
            Interlocked.Exchange(ref this._quitDisconnectPending, 0);
        }
    }

    private void DisconnectForQuit()
    {
        try
        {
            if (Volatile.Read(ref this._disposeState) != 0)
            {
                return;
            }

            if (this._processExitSubscription is { IsAttached: true } subscription)
            {
                this._quittingProcessId = subscription.ProcessId;
            }

            this.Disconnect();
        }
        catch (Exception ex)
        {
            ITunesLog.ITunesUpdatePlaybackStateFailed(this._logger, ex);
        }
        finally
        {
            Interlocked.Exchange(ref this._quitDisconnectPending, 0);
        }
    }

    private void RequestStateRefresh()
    {
        if (Volatile.Read(ref this._disposeState) != 0 || Volatile.Read(ref this._quitDisconnectPending) != 0)
        {
            return;
        }

        if (!this._refreshScheduler.RequestRefresh())
        {
            return;
        }

        if (this._dispatcherQueue?.TryEnqueue(this.RunRequestedRefresh) != true)
        {
            this._refreshScheduler.Clear();
        }
    }

    private void RunRequestedRefresh()
    {
        if (!this._refreshScheduler.TryBeginRefresh())
        {
            return;
        }

        try
        {
            if (Volatile.Read(ref this._disposeState) == 0 && Volatile.Read(ref this._quitDisconnectPending) == 0)
            {
                bool isConnected;
                lock (this._stateLock)
                {
                    isConnected = this._isConnected;
                }

                if (isConnected)
                {
                    this.UpdatePlaybackAndMetadata();
                }
            }
        }
        finally
        {
            if (Volatile.Read(ref this._disposeState) == 0 && this._refreshScheduler.CompleteRefresh())
            {
                if (this._dispatcherQueue?.TryEnqueue(this.RunRequestedRefresh) != true)
                {
                    this._refreshScheduler.Clear();
                }
            }
        }
    }

    private void TrackConnectedProcess(Process process)
    {
        var subscription = new ITunesProcessExitSubscription();
        subscription.Attach(process, this.OnConnectedProcessExited);
        this._processExitSubscription = subscription;
    }

    private void OnConnectedProcessExited(int processId)
    {
        try
        {
            this._dispatcherQueue?.TryEnqueue(() => this.HandleConnectedProcessExited(processId));
        }
        catch
        {
            // Process.Exited can be raised on a runtime thread; it must not fault it.
        }
    }

    private void HandleConnectedProcessExited(int processId)
    {
        if (this._processExitSubscription?.ProcessId == processId)
        {
            this._quittingProcessId = null;
            this.Disconnect();
        }
    }

    private void Disconnect(bool resumeDiscovery = true)
    {
        var waitForProcessExit = resumeDiscovery && this._quittingProcessId != null;
        this._refreshScheduler.Clear();
        this.UnhookEvents();

        if (!waitForProcessExit)
        {
            this._processExitSubscription?.Dispose();
            this._processExitSubscription = null;
            this._quittingProcessId = null;
        }

        if (this._iTunesApp != 0)
        {
            _ = ITunesNative.Release(this._iTunesApp);
            this._iTunesApp = 0;
        }

        bool wasConnected;
        lock (this._stateLock)
        {
            wasConnected = this._isConnected;
            this._isConnected = false;
            this._currentMediaProperties = null;
            this._currentTimeline = MediaTimelinePropertiesSnapshot.Empty;
            this._currentPlaybackState = MediaPlaybackState.Stopped;
            this._currentCapabilities = MediaCapabilities.None;
            this._cachedArtwork = null;
            this._cachedArtworkTrackId = -1;
            this._artworkVersion++;
            this._bindingGeneration++;
            this._revision++;
        }

        if (wasConnected)
        {
            this.SignalChanged(MediaBackendSignal.ObservationsChanged | MediaBackendSignal.SessionsChanged | MediaBackendSignal.CurrentSessionChanged | MediaBackendSignal.BackendsChanged);
        }

        if (resumeDiscovery && !waitForProcessExit && Volatile.Read(ref this._disposeState) == 0)
        {
            this.StartDiscoveryPolling();
        }
        else
        {
            this.StopDiscoveryPolling();
        }
    }

    private void UnhookEvents()
    {
        if (this._connectionPoint != 0)
        {
            if (this._adviseCookie != 0)
            {
                var hr = ITunesNative.Unadvise(this._connectionPoint, this._adviseCookie);
                if (hr < 0)
                {
                    ITunesLog.ITunesUnhookEventsFailed(this._logger, hr);
                }

                this._adviseCookie = 0;
            }

            _ = ITunesNative.Release(this._connectionPoint);
            this._connectionPoint = 0;
        }

        if (this._eventSink != null)
        {
            this._eventSink.Dispose();
            this._eventSink = null;
        }
    }

    private bool IsDisconnectRequested =>
        Volatile.Read(ref this._quitDisconnectPending) != 0 || Volatile.Read(ref this._disposeState) != 0;

    private void ThrowIfDisconnectRequested()
    {
        if (this.IsDisconnectRequested)
        {
            throw new OperationCanceledException("iTunes is disconnecting.");
        }
    }

    // A native call can deliver a quit callback before returning to its caller.
    private nint GetComCallTarget(nint instance)
    {
        this.ThrowIfDisconnectRequested();
        return instance;
    }

    private void UpdatePlaybackAndMetadata()
    {
        if (this._iTunesApp == 0 || this.IsDisconnectRequested)
        {
            return;
        }

        try
        {
            var hr = ITunesNative.GetPlayerState(this.GetComCallTarget(this._iTunesApp), out var rawState);
            this.ThrowIfDisconnectRequested();
            if (hr < 0)
            {
                this.Disconnect();
                return;
            }

            var playbackState = rawState switch
            {
                ITPlayerState.Playing => MediaPlaybackState.Playing,
                ITPlayerState.Stopped => MediaPlaybackState.Stopped,
                _ => MediaPlaybackState.Paused,
            };

            var capabilities = MediaCapabilities.Play |
                               MediaCapabilities.Pause |
                               MediaCapabilities.Stop |
                               MediaCapabilities.SkipNext |
                               MediaCapabilities.SkipPrevious;

            nint pPlaylist = 0;
            _ = ITunesNative.GetCurrentPlaylist(this.GetComCallTarget(this._iTunesApp), out pPlaylist);
            if (pPlaylist != 0)
            {
                try
                {
                    var shuffleHr = ITunesNative.GetPlaylistShuffle(this.GetComCallTarget(pPlaylist), out _);
                    if (shuffleHr >= 0)
                    {
                        capabilities |= MediaCapabilities.ToggleShuffle;
                    }

                    var repeatHr = ITunesNative.GetPlaylistRepeat(this.GetComCallTarget(pPlaylist), out _);
                    if (repeatHr >= 0)
                    {
                        capabilities |= MediaCapabilities.ToggleRepeat;
                    }
                }
                finally
                {
                    _ = ITunesNative.Release(pPlaylist);
                }
            }

            string title = string.Empty;
            string artist = string.Empty;
            string album = string.Empty;
            string genre = string.Empty;
            int durationSec = 0;
            int trackNumber = 0;
            int trackCount = 0;
            int positionSec = 0;
            int currentTrackId = 0;

            nint pTrack = 0;
            hr = ITunesNative.GetCurrentTrack(this.GetComCallTarget(this._iTunesApp), out pTrack);
            bool trackChanged = false;
            try
            {
                this.ThrowIfDisconnectRequested();
                if (hr < 0 || pTrack == 0)
                {
                    this.ClearCurrentTrack();
                    return;
                }

                _ = ITunesNative.GetTrackName(this.GetComCallTarget(pTrack), out title);
                _ = ITunesNative.GetTrackArtist(this.GetComCallTarget(pTrack), out artist);
                _ = ITunesNative.GetTrackAlbum(this.GetComCallTarget(pTrack), out album);
                _ = ITunesNative.GetTrackGenre(this.GetComCallTarget(pTrack), out genre);
                _ = ITunesNative.GetTrackDuration(this.GetComCallTarget(pTrack), out durationSec);
                _ = ITunesNative.GetTrackNumber(this.GetComCallTarget(pTrack), out trackNumber);
                _ = ITunesNative.GetTrackCount(this.GetComCallTarget(pTrack), out trackCount);
                _ = ITunesNative.GetTrackDatabaseId(this.GetComCallTarget(pTrack), out currentTrackId);
                this.ThrowIfDisconnectRequested();

                lock (this._stateLock)
                {
                    trackChanged = currentTrackId != this._cachedArtworkTrackId;
                    if (trackChanged)
                    {
                        // A track change immediately invalidates any cached artwork and bumps the version.
                        this._cachedArtwork = null;
                        this._cachedArtworkTrackId = currentTrackId;
                        this._artworkVersion++;
                    }
                }

                if (trackChanged && currentTrackId != 0)
                {
                    this.UpdateCachedArtwork(pTrack, currentTrackId);
                }
            }
            finally
            {
                _ = ITunesNative.Release(pTrack);
            }

            _ = ITunesNative.GetPlayerPosition(this.GetComCallTarget(this._iTunesApp), out positionSec);
            this.ThrowIfDisconnectRequested();

            MediaArtworkKey? artworkKey = null;
            lock (this._stateLock)
            {
                if (this._cachedArtwork != null && this._cachedArtworkTrackId == currentTrackId && currentTrackId != 0)
                {
                    artworkKey = new MediaArtworkKey(new MediaSessionId(DefaultSessionId.Value), this._artworkVersion);
                }
            }

            var source = this.CreateSourceSnapshot();

            var properties = MediaPropertiesSnapshot.Empty(source) with
            {
                Title = title,
                Artist = artist,
                AlbumTitle = album,
                Genres = string.IsNullOrEmpty(genre) ? [] : [genre],
                TrackNumber = trackNumber,
                AlbumTrackCount = trackCount,
                ContentType = MediaContentType.Music,
                Artwork = artworkKey,
            };

            var timeline = new MediaTimelinePropertiesSnapshot(
                StartTime: TimeSpan.Zero,
                EndTime: TimeSpan.FromSeconds(durationSec),
                MinSeekTime: TimeSpan.Zero,
                MaxSeekTime: TimeSpan.FromSeconds(durationSec),
                Position: TimeSpan.FromSeconds(positionSec),
                LastUpdatedAt: DateTimeOffset.UtcNow);

            bool stateChanged = false;
            bool sessionAppeared;
            lock (this._stateLock)
            {
                sessionAppeared = this._currentMediaProperties == null;
                if (trackChanged ||
                    sessionAppeared ||
                    this._currentMediaProperties?.Artwork != properties.Artwork ||
                    this._currentPlaybackState != playbackState ||
                    this._currentCapabilities != capabilities ||
                    this._currentMediaProperties?.Title != title ||
                    this._currentMediaProperties?.Artist != artist ||
                    this._currentMediaProperties?.AlbumTitle != album ||
                    Math.Abs((this._currentTimeline.Position - timeline.Position).TotalSeconds) > 1.5)
                {
                    stateChanged = true;
                    this._revision++;
                }

                if (sessionAppeared)
                {
                    this._bindingGeneration++;
                }

                this._currentPlaybackState = playbackState;
                this._currentCapabilities = capabilities;
                this._currentMediaProperties = properties;
                this._currentTimeline = timeline;
            }

            if (stateChanged)
            {
                var signal = MediaBackendSignal.ObservationsChanged | MediaBackendSignal.CurrentSessionChanged;
                if (sessionAppeared)
                {
                    signal |= MediaBackendSignal.SessionsChanged;
                }

                this.SignalChanged(signal);
            }
        }
        catch (OperationCanceledException) when (this.IsDisconnectRequested)
        {
        }
        catch (Exception ex)
        {
            ITunesLog.ITunesUpdatePlaybackStateFailed(this._logger, ex);
        }
    }

    private void ClearCurrentTrack()
    {
        bool sessionRemoved;
        lock (this._stateLock)
        {
            sessionRemoved = this._currentMediaProperties != null;
            var hadArtwork = this._cachedArtwork != null || this._cachedArtworkTrackId != -1;

            this._currentMediaProperties = null;
            this._currentTimeline = MediaTimelinePropertiesSnapshot.Empty;
            this._currentPlaybackState = MediaPlaybackState.Stopped;
            this._currentCapabilities = MediaCapabilities.None;
            this._cachedArtwork = null;
            this._cachedArtworkTrackId = -1;

            if (hadArtwork)
            {
                this._artworkVersion++;
            }

            if (sessionRemoved)
            {
                this._bindingGeneration++;
                this._revision++;
            }
        }

        if (sessionRemoved)
        {
            this.SignalChanged(MediaBackendSignal.ObservationsChanged | MediaBackendSignal.SessionsChanged | MediaBackendSignal.CurrentSessionChanged);
        }
    }

    private void UpdateCachedArtwork(nint pTrack, int trackDatabaseId)
    {
        nint pArtworks = 0;
        _ = ITunesNative.GetTrackArtworkCollection(this.GetComCallTarget(pTrack), out pArtworks);
        if (pArtworks == 0)
        {
            return;
        }

        try
        {
            nint pArtwork = 0;
            _ = ITunesNative.GetArtworkItem(this.GetComCallTarget(pArtworks), 1, out pArtwork);
            if (pArtwork == 0)
            {
                return;
            }

            try
            {
                _ = ITunesNative.GetArtworkFormat(this.GetComCallTarget(pArtwork), out var format);
                var tempFile = Path.Combine(Path.GetTempPath(), $"MC_iTunes_Art_{Guid.NewGuid():N}.tmp");

                try
                {
                    var saveHr = ITunesNative.SaveArtworkToFile(this.GetComCallTarget(pArtwork), tempFile);
                    this.ThrowIfDisconnectRequested();
                    if (saveHr >= 0 && File.Exists(tempFile))
                    {
                        var bytes = File.ReadAllBytes(tempFile);
                        var contentType = format switch
                        {
                            ITArtworkFormat.PNG => "image/png",
                            ITArtworkFormat.BMP => "image/bmp",
                            _ => "image/jpeg",
                        };

                        var hash = Convert.ToHexString(SHA256.HashData(bytes));
                        lock (this._stateLock)
                        {
                            if (this._cachedArtworkTrackId == trackDatabaseId)
                            {
                                this._cachedArtwork = new MediaArtworkContent(contentType, bytes, hash);
                                this._artworkVersion++;
                            }
                        }
                    }
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempFile))
                        {
                            File.Delete(tempFile);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                _ = ITunesNative.Release(pArtwork);
            }
        }
        finally
        {
            _ = ITunesNative.Release(pArtworks);
        }
    }

    private int ExecuteToggleShuffle()
    {
        nint pPlaylist = 0;
        var hr = ITunesNative.GetCurrentPlaylist(this.GetComCallTarget(this._iTunesApp), out pPlaylist);
        if (hr < 0 || pPlaylist == 0)
        {
            return hr;
        }

        try
        {
            hr = ITunesNative.GetPlaylistShuffle(this.GetComCallTarget(pPlaylist), out var currentShuffle);
            if (hr >= 0)
            {
                hr = ITunesNative.SetPlaylistShuffle(this.GetComCallTarget(pPlaylist), !currentShuffle);
            }

            return hr;
        }
        finally
        {
            _ = ITunesNative.Release(pPlaylist);
        }
    }

    private int ExecuteToggleRepeat()
    {
        nint pPlaylist = 0;
        var hr = ITunesNative.GetCurrentPlaylist(this.GetComCallTarget(this._iTunesApp), out pPlaylist);
        if (hr < 0 || pPlaylist == 0)
        {
            return hr;
        }

        try
        {
            hr = ITunesNative.GetPlaylistRepeat(this.GetComCallTarget(pPlaylist), out var currentRepeat);
            if (hr >= 0)
            {
                var nextRepeat = currentRepeat switch
                {
                    ITPlaylistRepeatMode.Off => ITPlaylistRepeatMode.All,
                    ITPlaylistRepeatMode.All => ITPlaylistRepeatMode.One,
                    _ => ITPlaylistRepeatMode.Off,
                };

                hr = ITunesNative.SetPlaylistRepeat(this.GetComCallTarget(pPlaylist), nextRepeat);
            }

            return hr;
        }
        finally
        {
            _ = ITunesNative.Release(pPlaylist);
        }
    }

    private void ResolveExecutablePath()
    {
        try
        {
            var defaultPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "iTunes",
                "iTunes.exe");

            if (File.Exists(defaultPath))
            {
                lock (this._stateLock)
                {
                    this._executablePath = defaultPath;
                }
            }
        }
        catch
        {
        }
    }

    private void RecordExecutablePath(Process process)
    {
        try
        {
            if (process.MainModule?.FileName is { } path && File.Exists(path))
            {
                lock (this._stateLock)
                {
                    this._executablePath = path;
                }
            }
        }
        catch
        {
        }
    }

    private MediaSourceSnapshot CreateSourceSnapshot() =>
        new(
            DisplayName: "iTunes",
            IconPath: this._sourceIconPath)
        {
            NativeApplication = CreateNativeApplicationIdentity(this._executablePath),
        };

    internal static MediaNativeApplicationIdentity CreateNativeApplicationIdentity(string? executablePath) =>
        new(IsStoreInstallation(executablePath) ? StoreApplicationId : DirectInstallerApplicationId, executablePath);

    private static bool IsStoreInstallation(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        var windowsApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsApps");
        var windowsAppsPrefix = Path.EndsInDirectorySeparator(windowsApps)
            ? windowsApps
            : windowsApps + Path.DirectorySeparatorChar;

        return executablePath.StartsWith(windowsAppsPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private void SignalChanged(MediaBackendSignal signal)
    {
        Interlocked.Or(ref this._pendingSignals, (int)signal);
        this._signals.Writer.TryWrite(true);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this._disposeState, 1) != 0)
        {
            return;
        }

        this._lifetime.Cancel();
        this._signals.Writer.TryComplete();
        this._refreshScheduler.Clear();
        Interlocked.Exchange(ref this._quitDisconnectPending, 1);

        if (this._dispatcherQueue != null)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var enqueued = this._dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    this.StopDiscoveryPolling();
                    this.Disconnect(resumeDiscovery: false);
                }
                finally
                {
                    tcs.TrySetResult();
                }
            });

            if (enqueued)
            {
                await tcs.Task.ConfigureAwait(false);
            }
        }

        if (this._dispatcherController != null)
        {
            await this._dispatcherController.ShutdownQueueAsync();
        }

        this._lifetime.Dispose();
    }
}
