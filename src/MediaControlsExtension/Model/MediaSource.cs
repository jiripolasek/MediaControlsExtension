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
    internal readonly record struct PlaybackPrediction(long Generation);
    internal sealed record MediaUpdateRequest(bool UpdatePlayback, bool UpdateMediaProperties);

    private static readonly TimeSpan PlaybackActionPredictionLifetime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PlaybackReconciliationLifetime = TimeSpan.FromSeconds(2);

    private readonly Lock _playbackStateLock = new();
    private readonly CoalescingAsyncLoader<MediaUpdateRequest, object> _mediaUpdateLoader;
    private readonly CoalescingAsyncLoader<IRandomAccessStreamReference?, ThumbnailInfo> _thumbnailLoader;
    private volatile bool _disposed;
    private long _nextPlaybackPredictionGeneration;
    private PendingPlaybackPrediction? _pendingPlaybackPrediction;

    public event EventHandler? MediaPropertiesUpdated;
    public event EventHandler? PlaybackInfoChanged;
    public event EventHandler? PlaybackPresentationChanged;
    public event EventHandler? ThumbnailChanged;

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

    internal bool UpdateSessionUnderGate(GlobalSystemMediaTransportControlsSession session)
    {
        GsmtcOperationGate.VerifyAccess();
        ArgumentNullException.ThrowIfNull(session);

        if (this._disposed || ReferenceEquals(this.Session, session))
        {
            return false;
        }

        if (!string.Equals(
            this.SourceAppUserModelId,
            session.SourceAppUserModelId,
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
        }
        catch
        {
            // Ignore errors during unsubscription
        }
    }

    public MediaSource(GlobalSystemMediaTransportControlsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        this.Session = session;
        this.SourceAppUserModelId = session.SourceAppUserModelId;

        this._thumbnailLoader = new(
            static (reference, token) => ThumbnailLoader.LoadAsync(reference, 20, 20, token),
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

        this._mediaUpdateLoader = new(
            async (request, token) =>
            {
                var presentationChanged = await GsmtcOperationGate.RunAsync(
                    cancellationToken => this.UpdatePropertiesFromSession(
                        this.Session,
                        request.UpdatePlayback,
                        request.UpdateMediaProperties,
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
                pending.UpdateMediaProperties || next.UpdateMediaProperties)
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

        this.QueueUnhookSession();

        this._thumbnailLoader.Dispose();
        this._mediaUpdateLoader.Dispose();

        this.ThumbnailInfo = null;
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
        this.TriggerUpdate(true, false);
    }

    private void SessionOnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args)
    {
        this.TriggerUpdate(true, true);
    }

    private void TriggerUpdate(bool playback, bool mediaProps)
    {
        if (this._disposed || GsmtcOperationGate.IsCircuitOpen)
        {
            return;
        }

        this._mediaUpdateLoader.Schedule(new(playback, mediaProps));
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

            prediction = new(++this._nextPlaybackPredictionGeneration);
            this._pendingPlaybackPrediction?.Expiration.Cancel();
            expiration = new();
            this._pendingPlaybackPrediction = new(prediction.Generation, predictedIsPlaying, expiration);
            this.DisplayedIsPlaying = predictedIsPlaying;
        }

        this.PlaybackPresentationChanged?.Invoke(this, EventArgs.Empty);
        _ = this.ExpirePlaybackPredictionAsync(prediction, PlaybackActionPredictionLifetime, expiration);
        return prediction;
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

    private void ScheduleThumbnailUpdate(IRandomAccessStreamReference? thumbnailRef)
    {
        this._thumbnailLoader.Schedule(thumbnailRef);
    }

    private async Task<bool> UpdatePropertiesFromSession(
        GlobalSystemMediaTransportControlsSession session,
        bool updatePlayback,
        bool updateMediaProperties,
        CancellationToken cancellationToken)
    {
        GsmtcOperationGate.VerifyAccess();

        var presentationChanged = false;

        if (this.AppInfo == null)
        {
            try
            {
                this.AppInfo = UpdateAppDisplayInfo(session);
                this.ApplicationName = this.AppInfo.DisplayName ?? "";
                this.ApplicationIconPath = this.AppInfo.IconPath;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }
        }

        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            presentationChanged = this.ApplyConfirmedPlaybackInfo(playbackInfo);

            if (updateMediaProperties)
            {
                var mediaProperties = await session.TryGetMediaPropertiesAsync()!;
                if (mediaProperties != null)
                {
                    this.HasProperties = true;
                    this.Name = mediaProperties.Title ?? string.Empty;
                    this.Artist = mediaProperties.Artist ?? string.Empty;
                    this.PlaybackType = mediaProperties.PlaybackType ?? MediaPlaybackType.Unknown;
                    if (mediaProperties.Thumbnail != null)
                    {
                        this.ScheduleThumbnailUpdate(mediaProperties.Thumbnail);
                    }
                }
                else
                {
                    this.HasProperties = false;
                    this.Name = string.Empty;
                    this.Artist = string.Empty;
                    this.PlaybackType = MediaPlaybackType.Unknown;
                    this.ThumbnailInfo = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ignore this exception, it is expected when the task is cancelled
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to update properties for " + session.SourceAppUserModelId, ex);
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

            if (this._pendingPlaybackPrediction?.PredictedIsPlaying == confirmedIsPlaying)
            {
                this._pendingPlaybackPrediction.Expiration.Cancel();
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

    private static IAppInfo UpdateAppDisplayInfo(GlobalSystemMediaTransportControlsSession session)
    {
        if (string.IsNullOrWhiteSpace(session.SourceAppUserModelId))
        {
            return EmptyAppInfo.Instance;
        }

        var appInfo = ModernAppHelper.Get(session.SourceAppUserModelId);
        if (appInfo != null)
        {
            var appDisplayInfo = appInfo.DisplayInfo;
            if (appDisplayInfo != null)
            {
                return new ModernAppInfo(appInfo, PackageIconHelper.GetBestIconPath(session.SourceAppUserModelId));
            }
        }

        var desktopApp = DesktopAppHelper.GetExecutable(session.SourceAppUserModelId);
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
        this.TriggerUpdate(true, true);
    }
}