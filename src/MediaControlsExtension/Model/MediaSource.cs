// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Runtime.CompilerServices;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace JPSoftworks.MediaControlsExtension.Model;

internal sealed partial class MediaSource : BaseObservable, IDisposable
{
    internal readonly record struct PlaybackPrediction(long Generation, bool PredictedIsPlaying);
    internal readonly record struct PlaybackPresentationState(
        bool IsPredictionPending,
        bool IsPlaying,
        bool DisplayedIsPlaying,
        bool CanPause,
        bool CanStop);
    internal sealed record MediaUpdateRequest(
        bool UpdatePlayback,
        bool UpdateMediaProperties,
        bool UpdateTimeline);

    private static readonly TimeSpan PlaybackActionPredictionLifetime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PlaybackReconciliationLifetime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StaleSessionRetryDelay = TimeSpan.FromMilliseconds(350);
    private const uint ListThumbnailMaxDimension = 40;
    private const uint ThumbnailMaxDimension = 512;

    private readonly Lock _playbackStateLock = new();
    private readonly Lock _thumbnailStateLock = new();
    private readonly CoalescingAsyncLoader<MediaUpdateRequest, object> _mediaUpdateLoader;
    private readonly CoalescingAsyncLoader<IRandomAccessStreamReference?, ThumbnailInfo> _thumbnailLoader;
    private readonly CoalescingAsyncLoader<IRandomAccessStreamReference?, ThumbnailInfo> _heroThumbnailLoader;
    private volatile bool _disposed;
    private bool _heroThumbnailRequested;
    private IRandomAccessStreamReference? _heroThumbnailReference;
    private IRandomAccessStreamReference? _thumbnailReference;
    private long _nextPlaybackPredictionGeneration;
    private PendingPlaybackPrediction? _pendingPlaybackPrediction;

    private readonly Lock _playbackQueueLock = new();
    private readonly CancellationTokenSource _playbackActionCts = new();
    private readonly CancellationToken _playbackActionToken;
    private QueuedPlaybackAction? _queuedPlaybackAction;
    private long _lastQueuedPlaybackGeneration;
    private bool _playbackWorkerRunning;

    public event EventHandler? MediaPropertiesUpdated;
    public event EventHandler? PlaybackInfoChanged;
    public event EventHandler? PlaybackPresentationChanged;
    public event EventHandler? ThumbnailChanged;

    /// <summary>
    /// Raised when a queued playback action hit a dead session (E_BOUNDS /
    /// RO_E_CLOSED) so the owning service can re-resolve sessions.
    /// </summary>
    public event EventHandler? SessionInvalidated;

    public bool HasProperties
    {
        get;
        private set => this.SetField(ref field, value);
    }

    public string Name
    {
        get;
        set => this.SetField(ref field, value);
    } = "";

    public string Artist
    {
        get;
        set => this.SetField(ref field, value);
    } = "";

    public string AlbumTitle
    {
        get;
        private set => this.SetField(ref field, value);
    } = "";

    public string AlbumArtist
    {
        get;
        private set => this.SetField(ref field, value);
    } = "";

    public string Subtitle
    {
        get;
        private set => this.SetField(ref field, value);
    } = "";

    public string Genres
    {
        get;
        private set => this.SetField(ref field, value);
    } = "";

    public int TrackNumber
    {
        get;
        private set => this.SetField(ref field, value);
    }

    public int AlbumTrackCount
    {
        get;
        private set => this.SetField(ref field, value);
    }

    public TimeSpan? TrackLength
    {
        get;
        private set => this.SetField(ref field, value);
    }

    public bool IsPlaying
    {
        get;
        private set => this.SetField(ref field, value);
    }

    public bool DisplayedIsPlaying
    {
        get;
        private set => this.SetField(ref field, value);
    }

    public bool CanPause
    {
        get;
        private set => this.SetField(ref field, value);
    }

    public bool CanStop
    {
        get;
        private set => this.SetField(ref field, value);
    }

    public bool CanSkipNext
    {
        get;
        private set => this.SetField(ref field, value);
    }

    public bool CanSkipPrevious
    {
        get;
        private set => this.SetField(ref field, value);
    }

    public bool CanToggleShuffle
    {
        get;
        private set => this.SetField(ref field, value);
    }

    public bool CanToggleRepeat
    {
        get;
        private set => this.SetField(ref field, value);
    }

    public string? ApplicationIconPath
    {
        get;
        private set => this.SetField(ref field, value);
    }

    public string? ApplicationName
    {
        get;
        private set => this.SetField(ref field, value);
    }

    public ThumbnailInfo? ThumbnailInfo
    {
        get;
        private set => this.SetField(ref field, value);
    }

    public ThumbnailInfo? HeroThumbnailInfo
    {
        get;
        private set => this.SetField(ref field, value);
    }

    public IAppInfo? AppInfo
    {
        get;
        private set => this.SetField(ref field, value);
    }

    public MediaPlaybackType PlaybackType
    {
        get;
        private set => this.SetField(ref field, value);
    }

    public string SourceAppUserModelId { get; }

    public GlobalSystemMediaTransportControlsSession Session { get; private set; }

    private int _eventsSubscribed;

    internal void HookSessionUnderGate()
    {
        GsmtcOperationGate.VerifyAccess();

        if (this._disposed)
        {
            return;
        }

        var session = this.Session;
        try
        {
            session.MediaPropertiesChanged += this.SessionOnMediaPropertiesChanged;
            session.PlaybackInfoChanged += this.SessionOnPlaybackInfoChanged;
            session.TimelinePropertiesChanged += this.SessionOnTimelinePropertiesChanged;
            Volatile.Write(ref this._eventsSubscribed, 1);

            if (this._disposed && Interlocked.Exchange(ref this._eventsSubscribed, 0) != 0)
            {
                this.UnhookSessionCore(session);
            }
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref this._eventsSubscribed, 0);
            this.UnhookSessionCore(session);
            Logger.LogWarning($"Could not subscribe to events for {this.SourceAppUserModelId}: {ex.Message}");
        }
    }

    internal bool UpdateSessionUnderGate(GlobalSystemMediaTransportControlsSession session, string sourceAppUserModelId)
    {
        GsmtcOperationGate.VerifyAccess();
        ArgumentNullException.ThrowIfNull(session);

        if (this._disposed || ReferenceEquals(this.Session, session))
        {
            return false;
        }

        // The caller supplies the AUMID it already read so a dead session
        // cannot fault this guard with a native re-read.
        if (!string.Equals(
            this.SourceAppUserModelId,
            sourceAppUserModelId,
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A MediaSource cannot be rebound to a different source application.");
        }

        this.UnhookSessionUnderGate();

        if (this._disposed)
        {
            return false;
        }

        this.Session = session;
        this.HookSessionUnderGate();
        return !this._disposed;
    }

    private void UnhookSessionUnderGate()
    {
        GsmtcOperationGate.VerifyAccess();

        if (Interlocked.Exchange(ref this._eventsSubscribed, 0) == 0)
        {
            return;
        }

        this.UnhookSessionCore(this.Session);
    }

    private void QueueUnhookSession()
    {
        if (Interlocked.Exchange(ref this._eventsSubscribed, 0) == 0)
        {
            return;
        }

        var session = this.Session;
        _ = GsmtcOperationGate.RunDetached(() => this.UnhookSessionAsync(session));
    }

    private async Task UnhookSessionAsync(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            await GsmtcOperationGate.RunAsync(() => this.UnhookSessionCore(session));
        }
        catch (GsmtcCircuitOpenException)
        {
            // The process must be restarted before native cleanup is safe.
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Could not unsubscribe from events for {this.SourceAppUserModelId}: {ex.Message}");
        }
    }

    private void UnhookSessionCore(GlobalSystemMediaTransportControlsSession session)
    {
        GsmtcOperationGate.VerifyAccess();

        try
        {
            session.MediaPropertiesChanged -= this.SessionOnMediaPropertiesChanged;
            session.PlaybackInfoChanged -= this.SessionOnPlaybackInfoChanged;
            session.TimelinePropertiesChanged -= this.SessionOnTimelinePropertiesChanged;
        }
        catch
        {
            // Ignore errors during unsubscription
        }
    }

    public MediaSource(GlobalSystemMediaTransportControlsSession session, string sourceAppUserModelId)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sourceAppUserModelId);

        this.Session = session;
        this.SourceAppUserModelId = sourceAppUserModelId;
        this._playbackActionToken = this._playbackActionCts.Token;

        this._thumbnailLoader = new(
            static (reference, token) => ThumbnailLoader.LoadAsync(reference, ListThumbnailMaxDimension, ListThumbnailMaxDimension, token),
            info =>
            {
                if (info != this.ThumbnailInfo)
                {
                    this.ThumbnailInfo = info;
                    this.ThumbnailChanged?.Invoke(this, EventArgs.Empty);
                }
            },
            // CmdPal can retain published thumbnail streams through IconInfo.FromStream.
            _ => { }
        );

        this._heroThumbnailLoader = new(
            static (reference, token) => ThumbnailLoader.LoadAsync(reference, ThumbnailMaxDimension, ThumbnailMaxDimension, token),
            info => this.HeroThumbnailInfo = info,
            // CmdPal can retain published thumbnail streams through IconInfo.FromStream.
            _ => { }
        );

        this._mediaUpdateLoader = new(
            async (request, token) =>
            {
                var presentationChanged = await GsmtcOperationGate.RunAsync(
                    cancellationToken => this.UpdatePropertiesFromSession(
                        this.Session,
                        request.UpdatePlayback,
                        request.UpdateMediaProperties,
                        request.UpdateTimeline,
                        cancellationToken),
                    token);

                if (presentationChanged)
                {
                    // Handlers update list items synchronously and notify the host;
                    // that fan-out must not run under the gate's watchdog.
                    this.PlaybackPresentationChanged?.Invoke(this, EventArgs.Empty);
                }

                return null;
            },
            _ => { },
            _ => { },
            static (pending, next) => new(
                pending.UpdatePlayback || next.UpdatePlayback,
                pending.UpdateMediaProperties || next.UpdateMediaProperties,
                pending.UpdateTimeline || next.UpdateTimeline)
        );

    }

    public void Dispose()
    {
        lock (this._playbackStateLock)
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            this._pendingPlaybackPrediction?.Expiration.Cancel();
            this._pendingPlaybackPrediction = null;
        }

        this._playbackActionCts.Cancel();

        lock (this._playbackQueueLock)
        {
            this._queuedPlaybackAction = null;
        }

        this.QueueUnhookSession();

        lock (this._thumbnailStateLock)
        {
            this._heroThumbnailRequested = false;
            this._heroThumbnailReference = null;
            this._thumbnailReference = null;
        }

        this._thumbnailLoader.Dispose();
        this._heroThumbnailLoader.Dispose();
        this._mediaUpdateLoader.Dispose();
        this._playbackActionCts.Dispose();

        this.ThumbnailInfo = null;
        this.HeroThumbnailInfo = null;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        this.OnPropertyChanged(propertyName!);
    }

    private void SessionOnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args)
    {
        this.TriggerUpdate(true, false, false);
    }

    private void SessionOnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args)
    {
        this.TriggerUpdate(true, true, true);
    }

    private void SessionOnTimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args)
    {
        this.TriggerUpdate(false, false, true);
    }

    private void TriggerUpdate(bool playback, bool mediaProps, bool timeline)
    {
        if (this._disposed || GsmtcOperationGate.IsCircuitOpen)
        {
            return;
        }

        this._mediaUpdateLoader.Schedule(new(playback, mediaProps, timeline));
    }

    internal PlaybackPrediction? BeginPlaybackPrediction(PlaybackIntent intent)
    {
        PlaybackPrediction prediction;
        CancellationTokenSource expiration;
        lock (this._playbackStateLock)
        {
            if (this._disposed)
            {
                return null;
            }

            var predictedIsPlaying = intent switch
            {
                PlaybackIntent.Play => true,
                PlaybackIntent.Pause or PlaybackIntent.Stop => false,
                _ => !this.DisplayedIsPlaying
            };

            prediction = new(++this._nextPlaybackPredictionGeneration, predictedIsPlaying);
            this._pendingPlaybackPrediction?.Expiration.Cancel();
            expiration = new();
            this._pendingPlaybackPrediction = new(prediction.Generation, predictedIsPlaying, expiration);
            this.DisplayedIsPlaying = predictedIsPlaying;
        }

        this.PlaybackPresentationChanged?.Invoke(this, EventArgs.Empty);
        _ = this.ExpirePlaybackPredictionAsync(prediction, PlaybackActionPredictionLifetime, expiration);
        return prediction;
    }

    internal PlaybackPresentationState GetPlaybackPresentationState()
    {
        lock (this._playbackStateLock)
        {
            return new(
                this._pendingPlaybackPrediction is not null,
                this.IsPlaying,
                this.DisplayedIsPlaying,
                this.CanPause,
                this.CanStop);
        }
    }

    internal void CompletePlaybackPrediction(PlaybackPrediction prediction, bool success)
    {
        var presentationChanged = false;
        CancellationTokenSource? reconciliationExpiration = null;
        lock (this._playbackStateLock)
        {
            var pendingPrediction = this._pendingPlaybackPrediction;
            if (pendingPrediction?.Generation != prediction.Generation)
            {
                return;
            }

            if (success)
            {
                pendingPrediction.Expiration.Cancel();
                reconciliationExpiration = new();
                pendingPrediction.Expiration = reconciliationExpiration;
            }
            else
            {
                pendingPrediction.Expiration.Cancel();
                this._pendingPlaybackPrediction = null;
                this.DisplayedIsPlaying = this.IsPlaying;
                presentationChanged = true;
            }
        }

        if (presentationChanged)
        {
            this.PlaybackPresentationChanged?.Invoke(this, EventArgs.Empty);
        }

        if (reconciliationExpiration is not null)
        {
            _ = this.ExpirePlaybackPredictionAsync(prediction, PlaybackReconciliationLifetime, reconciliationExpiration);
        }
    }

    /// <summary>
    /// Queues a playback action derived from the optimistic display state.
    /// Rapid presses coalesce: only the newest action executes, so button spam
    /// produces a single trailing native call instead of a backlog of stale
    /// operations. Returns this press's absolute intent (for optimistic
    /// feedback), or null when the source is disposed.
    /// </summary>
    internal PlaybackIntent? EnqueuePlaybackAction(
        PlaybackIntent intent,
        Func<GlobalSystemMediaTransportControlsSession, PlaybackIntent, Task<bool>> executor)
    {
        ArgumentNullException.ThrowIfNull(executor);

        var prediction = this.BeginPlaybackPrediction(intent);
        if (prediction is null)
        {
            return null;
        }

        // Preserve the absolute action presented to the user. Re-resolving it
        // from a lagging capability snapshot can turn an optimistic Pause into
        // Stop and reset playback position.
        var absoluteIntent = intent switch
        {
            PlaybackIntent.Play or PlaybackIntent.Pause or PlaybackIntent.Stop => intent,
            _ => prediction.Value.PredictedIsPlaying
                ? PlaybackIntent.Play
                : PlaybackIntent.Pause
        };

        var startWorker = false;
        lock (this._playbackQueueLock)
        {
            if (this._disposed)
            {
                return null;
            }

            // Concurrent presses can reach this insert out of prediction
            // order; a press that lost the race must not replace (or re-run
            // after) a newer action — the display already follows the newest
            // prediction, so only the newest action may trail.
            if (prediction.Value.Generation > this._lastQueuedPlaybackGeneration)
            {
                this._lastQueuedPlaybackGeneration = prediction.Value.Generation;
                this._queuedPlaybackAction = new(absoluteIntent, prediction.Value, executor);
                if (!this._playbackWorkerRunning)
                {
                    this._playbackWorkerRunning = true;
                    startWorker = true;
                }
            }
        }

        if (startWorker)
        {
            _ = Task.Run(this.RunPlaybackWorkerAsync);
        }

        return absoluteIntent;
    }

    private async Task RunPlaybackWorkerAsync()
    {
        while (true)
        {
            QueuedPlaybackAction action;
            lock (this._playbackQueueLock)
            {
                if (this._disposed || this._queuedPlaybackAction is null)
                {
                    this._playbackWorkerRunning = false;
                    return;
                }

                action = this._queuedPlaybackAction;
                this._queuedPlaybackAction = null;
            }

            try
            {
                var success = await this.ExecutePlaybackActionAsync(action).ConfigureAwait(false);
                // Completing a superseded prediction is a no-op; only the
                // latest press controls the displayed state.
                this.CompletePlaybackPrediction(action.Prediction, success);
                this.Update();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }
        }
    }

    private async Task<bool> ExecutePlaybackActionAsync(QueuedPlaybackAction action)
    {
        var cancellationToken = this._playbackActionToken;
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            return await GsmtcOperationGate.RunAsync(
                _ => this.ExecutePlaybackActionUnderGateAsync(action),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (GsmtcCircuitOpenException)
        {
            return false;
        }
        catch (Exception ex) when (GsmtcErrors.IndicatesStaleSession(ex))
        {
            // The session died under us. Ask the service to re-resolve
            // sessions, give the rebind a moment, then retry once.
            this.SessionInvalidated?.Invoke(this, EventArgs.Empty);
            try
            {
                await Task.Delay(StaleSessionRetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (cancellationToken.IsCancellationRequested || GsmtcOperationGate.IsCircuitOpen)
            {
                return false;
            }

            try
            {
                return await GsmtcOperationGate.RunAsync(
                    _ => this.ExecutePlaybackActionUnderGateAsync(action),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception retryException)
            {
                Logger.LogWarning(
                    $"Playback action failed after session rebind for {this.SourceAppUserModelId}: {retryException.Message}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
            return false;
        }
    }

    private Task<bool> ExecutePlaybackActionUnderGateAsync(QueuedPlaybackAction action)
    {
        GsmtcOperationGate.VerifyAccess();

        return this._disposed || this._playbackActionToken.IsCancellationRequested
            ? Task.FromResult(false)
            : action.Executor(this.Session, action.Intent);
    }

    private void ScheduleThumbnailUpdate(IRandomAccessStreamReference? thumbnailRef)
    {
        bool loadHeroThumbnail;
        lock (this._thumbnailStateLock)
        {
            if (this._disposed)
            {
                return;
            }

            this._thumbnailReference = thumbnailRef;
            loadHeroThumbnail = this._heroThumbnailRequested
                && !ReferenceEquals(this._heroThumbnailReference, thumbnailRef);
            if (loadHeroThumbnail)
            {
                this._heroThumbnailReference = thumbnailRef;
            }
        }

        this._thumbnailLoader.Schedule(thumbnailRef);
        if (loadHeroThumbnail)
        {
            this._heroThumbnailLoader.Schedule(thumbnailRef);
        }
    }

    public void RequestHeroThumbnail()
    {
        IRandomAccessStreamReference? thumbnailReference;
        bool loadHeroThumbnail;
        lock (this._thumbnailStateLock)
        {
            if (this._disposed)
            {
                return;
            }

            this._heroThumbnailRequested = true;
            thumbnailReference = this._thumbnailReference;
            loadHeroThumbnail = thumbnailReference is not null
                && !ReferenceEquals(this._heroThumbnailReference, thumbnailReference);
            if (loadHeroThumbnail)
            {
                this._heroThumbnailReference = thumbnailReference;
            }
        }

        if (loadHeroThumbnail)
        {
            this._heroThumbnailLoader.Schedule(thumbnailReference!);
        }
    }

    private async Task<bool> UpdatePropertiesFromSession(
        GlobalSystemMediaTransportControlsSession session,
        bool updatePlayback,
        bool updateMediaProperties,
        bool updateTimeline,
        CancellationToken cancellationToken)
    {
        GsmtcOperationGate.VerifyAccess();

        var presentationChanged = false;

        if (this.AppInfo == null)
        {
            try
            {
                this.AppInfo = UpdateAppDisplayInfo(this.SourceAppUserModelId);
                this.ApplicationName = this.AppInfo.DisplayName ?? "";
                this.ApplicationIconPath = this.AppInfo.IconPath;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }
        }

        if (updateTimeline)
        {
            try
            {
                var timelineProperties = session.GetTimelineProperties();
                var trackLength = timelineProperties.EndTime - timelineProperties.StartTime;
                this.TrackLength = trackLength > TimeSpan.Zero ? trackLength : null;
            }
            catch (Exception ex)
            {
                // Use the cached AUMID: reading it from a dead session throws
                // E_BOUNDS from inside the catch block.
                Logger.LogError("Failed to update timeline properties for " + this.SourceAppUserModelId, ex);
            }
        }

        try
        {
            if (updatePlayback)
            {
                var playbackInfo = session.GetPlaybackInfo();
                presentationChanged = this.ApplyConfirmedPlaybackInfo(playbackInfo);
            }

            if (updateMediaProperties)
            {
                var mediaProperties = await session.TryGetMediaPropertiesAsync()!;
                if (mediaProperties != null)
                {
                    this.HasProperties = true;
                    this.Name = mediaProperties.Title ?? string.Empty;
                    this.Artist = mediaProperties.Artist ?? string.Empty;
                    this.AlbumTitle = mediaProperties.AlbumTitle ?? string.Empty;
                    this.AlbumArtist = mediaProperties.AlbumArtist ?? string.Empty;
                    this.Subtitle = mediaProperties.Subtitle ?? string.Empty;
                    this.Genres = string.Join(", ", mediaProperties.Genres);
                    this.TrackNumber = mediaProperties.TrackNumber;
                    this.AlbumTrackCount = mediaProperties.AlbumTrackCount;
                    this.PlaybackType = mediaProperties.PlaybackType ?? MediaPlaybackType.Unknown;
                    this.ScheduleThumbnailUpdate(mediaProperties.Thumbnail);
                }
                else
                {
                    this.HasProperties = false;
                    this.Name = string.Empty;
                    this.Artist = string.Empty;
                    this.AlbumTitle = string.Empty;
                    this.AlbumArtist = string.Empty;
                    this.Subtitle = string.Empty;
                    this.Genres = string.Empty;
                    this.TrackNumber = 0;
                    this.AlbumTrackCount = 0;
                    this.TrackLength = null;
                    this.PlaybackType = MediaPlaybackType.Unknown;
                    this.ScheduleThumbnailUpdate(null);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ignore this exception, it is expected when the task is cancelled
        }
        catch (Exception ex) when (GsmtcErrors.IndicatesStaleSession(ex))
        {
            // The session died; a refresh will rebind or remove this source.
            Logger.LogWarning($"Session for {this.SourceAppUserModelId} is no longer valid; requesting a refresh.");
            this.SessionInvalidated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to update properties for " + this.SourceAppUserModelId, ex);
        }

        if (updatePlayback)
        {
            this.PlaybackInfoChanged?.Invoke(this, EventArgs.Empty);
        }
        if (updateMediaProperties)
        {
            this.MediaPropertiesUpdated?.Invoke(this, EventArgs.Empty);
        }

        return presentationChanged;
    }

    /// <summary>
    /// Returns whether <see cref="PlaybackPresentationChanged"/> should be raised.
    /// The caller raises it outside the operation gate.
    /// </summary>
    private bool ApplyConfirmedPlaybackInfo(GlobalSystemMediaTransportControlsSessionPlaybackInfo? playbackInfo)
    {
        var confirmedIsPlaying = playbackInfo?.PlaybackStatus ==
                                 GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var presentationChanged = false;

        lock (this._playbackStateLock)
        {
            this.IsPlaying = confirmedIsPlaying;
            this.CanPause = playbackInfo?.Controls.IsPauseEnabled ?? false;
            this.CanStop = playbackInfo?.Controls.IsStopEnabled ?? false;
            this.CanSkipNext = playbackInfo?.Controls.IsNextEnabled ?? false;
            this.CanSkipPrevious = playbackInfo?.Controls.IsPreviousEnabled ?? false;
            this.CanToggleShuffle = playbackInfo?.Controls.IsShuffleEnabled ?? false;
            this.CanToggleRepeat = playbackInfo?.Controls.IsRepeatEnabled ?? false;

            var pendingPrediction = this._pendingPlaybackPrediction;
            var predictedStateConfirmed = pendingPrediction?.PredictedIsPlaying == confirmedIsPlaying;
            var presentationCaughtUp = !confirmedIsPlaying || this.CanPause;
            if (predictedStateConfirmed && presentationCaughtUp)
            {
                pendingPrediction!.Expiration.Cancel();
                this._pendingPlaybackPrediction = null;
            }

            var displayedIsPlaying = this._pendingPlaybackPrediction?.PredictedIsPlaying ?? confirmedIsPlaying;
            presentationChanged = this.DisplayedIsPlaying != displayedIsPlaying;
            this.DisplayedIsPlaying = displayedIsPlaying;
        }

        return presentationChanged;
    }

    private async Task ExpirePlaybackPredictionAsync(
        PlaybackPrediction prediction,
        TimeSpan delay,
        CancellationTokenSource expiration)
    {
        var cancellationToken = expiration.Token;
        try
        {
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var presentationChanged = false;
            lock (this._playbackStateLock)
            {
                if (this._disposed ||
                    this._pendingPlaybackPrediction?.Generation != prediction.Generation ||
                    !ReferenceEquals(this._pendingPlaybackPrediction.Expiration, expiration))
                {
                    return;
                }

                this._pendingPlaybackPrediction = null;
                this.DisplayedIsPlaying = this.IsPlaying;
                presentationChanged = true;
            }

            if (presentationChanged)
            {
                this.PlaybackPresentationChanged?.Invoke(this, EventArgs.Empty);
                this.Update();
            }
        }
        finally
        {
            expiration.Dispose();
        }
    }

    private sealed class PendingPlaybackPrediction(
        long generation,
        bool predictedIsPlaying,
        CancellationTokenSource expiration)
    {
        public long Generation { get; } = generation;
        public bool PredictedIsPlaying { get; } = predictedIsPlaying;
        public CancellationTokenSource Expiration { get; set; } = expiration;
    }

    private sealed record QueuedPlaybackAction(
        PlaybackIntent Intent,
        PlaybackPrediction Prediction,
        Func<GlobalSystemMediaTransportControlsSession, PlaybackIntent, Task<bool>> Executor);

    private static IAppInfo UpdateAppDisplayInfo(string sourceAppUserModelId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppUserModelId))
        {
            return EmptyAppInfo.Instance;
        }

        var appInfo = ModernAppHelper.Get(sourceAppUserModelId);
        if (appInfo != null)
        {
            var appDisplayInfo = appInfo.DisplayInfo;
            if (appDisplayInfo != null)
            {
                return new ModernAppInfo(appInfo, PackageIconHelper.GetBestIconPath(sourceAppUserModelId));
            }
        }

        var desktopApp = DesktopAppHelper.GetExecutable(sourceAppUserModelId);
        if (desktopApp is not null)
        {
            return desktopApp;
        }

        return EmptyAppInfo.Instance;
    }

    public override string ToString()
    {
        return $"MediaSource: AppId: {this.SourceAppUserModelId}, {nameof(this.Name)}: {this.Name}, {nameof(this.Artist)}: {this.Artist}, {nameof(this.IsPlaying)}: {this.IsPlaying}, {nameof(this.ApplicationIconPath)}: {this.ApplicationIconPath}, {nameof(this.ApplicationName)}: {this.ApplicationName}, {nameof(this.AppInfo)}: {this.AppInfo}, {nameof(this.PlaybackType)}: {this.PlaybackType}";
    }

    public void Update()
    {
        this.TriggerUpdate(true, true, true);
    }
}