// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using Windows.Media;

namespace JPSoftworks.MediaControlsExtension.ViewModels;

/// <summary>
/// Adds CmdPal-specific application and artwork presentation to a live media session.
/// Media state and command behavior remain owned by <see cref="MediaSession"/>.
/// </summary>
internal sealed partial class MediaSessionViewModel : IDisposable
{
    private readonly IMediaService _mediaService;
    private readonly Lock _applicationLock = new();
    private readonly Lock _artworkLock = new();

    private CancellationTokenSource? _artworkCancellation;
    private MediaArtworkKey? _artworkKey;
    private bool _artworkLoadCompleted;
    private bool _artworkRequested;
    private string? _applicationId;
    private long _applicationResolution;
    private Task _applicationResolutionTask = Task.CompletedTask;
    private bool _disposed;

    public MediaSessionViewModel(
        IMediaService mediaService,
        MediaSession session)
    {
        this._mediaService = mediaService ?? throw new ArgumentNullException(nameof(mediaService));
        this.Session = session ?? throw new ArgumentNullException(nameof(session));
        this.UpdateApplication(session.MediaProperties.Application);
        this._artworkKey = session.MediaProperties.Artwork;
        session.Changed += this.SessionOnChanged;
    }

    public event EventHandler? Changed;

    public MediaSession Session { get; }

    public bool IsAvailable => this.Session.IsAvailable;

    public MediaPropertiesSnapshot MediaProperties => this.Session.MediaProperties;

    public MediaTimelinePropertiesSnapshot TimelineProperties => this.Session.TimelineProperties;

    public MediaPlaybackInfoSnapshot PlaybackInfo => this.Session.PlaybackInfo;

    public string ApplicationName { get; private set; } = string.Empty;

    public string? ApplicationIconPath { get; private set; }

    public IAppInfo? AppInfo { get; private set; }

    public ThumbnailInfo? Artwork { get; private set; }

    public MediaPlaybackType PlaybackType => this.MediaProperties.ContentType switch
    {
        MediaContentType.Music => MediaPlaybackType.Music,
        MediaContentType.Video => MediaPlaybackType.Video,
        MediaContentType.Image => MediaPlaybackType.Image,
        _ => MediaPlaybackType.Unknown,
    };

    public async ValueTask<string> GetApplicationNameAsync(
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task resolutionTask;
            lock (this._applicationLock)
            {
                resolutionTask = this._applicationResolutionTask;
            }

            await resolutionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (this._applicationLock)
            {
                if (ReferenceEquals(resolutionTask, this._applicationResolutionTask))
                {
                    return this.ApplicationName;
                }
            }
        }
    }

    public void RequestArtwork()
    {
        lock (this._artworkLock)
        {
            if (this._disposed)
            {
                return;
            }

            this._artworkRequested = true;
            this.StartArtworkLoadUnderLock();
        }
    }

    public void Dispose()
    {
        lock (this._artworkLock)
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            this.CancelArtworkLoadUnderLock();
            this.Artwork = null;
        }

        this.Session.Changed -= this.SessionOnChanged;
        this.Changed = null;
    }

    private void SessionOnChanged(
        object? sender,
        MediaSessionChangedEventArgs args)
    {
        if ((args.Changes & (MediaSessionChanges.MediaProperties | MediaSessionChanges.Rebound)) != 0)
        {
            var properties = this.Session.MediaProperties;
            this.UpdateApplication(properties.Application);
            lock (this._artworkLock)
            {
                if (this._artworkKey != properties.Artwork)
                {
                    this.CancelArtworkLoadUnderLock();
                    this._artworkKey = properties.Artwork;
                    this._artworkLoadCompleted = properties.Artwork is null;
                    if (properties.Artwork is null)
                    {
                        this.Artwork = null;
                    }

                    this.StartArtworkLoadUnderLock();
                }
            }
        }

        this.RaiseChanged();
    }

    private void UpdateApplication(MediaApplicationSnapshot application)
    {
        lock (this._applicationLock)
        {
            if (string.Equals(
                this._applicationId,
                application.ApplicationId,
                StringComparison.Ordinal))
            {
                this.ApplyApplicationPresentation(application, this.AppInfo);
                return;
            }

            this._applicationId = application.ApplicationId;
            this.AppInfo = null;
            this.ApplyApplicationPresentation(application, null);

            var resolution = Interlocked.Increment(ref this._applicationResolution);
            this._applicationResolutionTask = Task.Run(
                () => this.ResolveApplicationInfo(application, resolution));
        }
    }

    private void ResolveApplicationInfo(
        MediaApplicationSnapshot application,
        long resolution)
    {
        try
        {
            var appInfo = ResolveAppInfo(application.ApplicationId);
            lock (this._applicationLock)
            {
                if (Volatile.Read(ref this._disposed) ||
                    resolution != Volatile.Read(ref this._applicationResolution))
                {
                    return;
                }

                this.AppInfo = appInfo;
                var currentApplication = this.Session.MediaProperties.Application;
                this.ApplyApplicationPresentation(currentApplication, appInfo);
            }

            this.RaiseChanged();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }
    }

    private void ApplyApplicationPresentation(
        MediaApplicationSnapshot application,
        IAppInfo? appInfo)
    {
        this.ApplicationName = string.IsNullOrWhiteSpace(appInfo?.DisplayName)
            ? application.DisplayName
            : appInfo.DisplayName;
        this.ApplicationIconPath = appInfo?.IconPath ?? application.IconPath;
    }

    private void StartArtworkLoadUnderLock()
    {
        if (!this._artworkRequested ||
            this._artworkCancellation is not null ||
            this._artworkLoadCompleted ||
            this._artworkKey is not { } artworkKey)
        {
            return;
        }

        this._artworkCancellation = new();
        _ = this.LoadArtworkAsync(artworkKey, this._artworkCancellation.Token);
    }

    private async Task LoadArtworkAsync(
        MediaArtworkKey artworkKey,
        CancellationToken cancellationToken)
    {
        ThumbnailInfo? thumbnail = null;
        var thumbnailPublished = false;
        try
        {
            var content = await this._mediaService.GetArtworkAsync(
                artworkKey,
                cancellationToken).ConfigureAwait(false);
            thumbnail = content is null
                ? null
                : await ThumbnailLoader.LoadAsync(content, cancellationToken).ConfigureAwait(false);

            var presentationChanged = false;
            lock (this._artworkLock)
            {
                if (this._disposed ||
                    cancellationToken.IsCancellationRequested ||
                    this._artworkKey != artworkKey)
                {
                    return;
                }

                var currentArtwork = this.Artwork;
                var isDuplicate =
                    thumbnail?.Hash is { Length: > 0 } hash &&
                    string.Equals(currentArtwork?.Hash, hash, StringComparison.Ordinal);

                if (!isDuplicate)
                {
                    presentationChanged = !ReferenceEquals(currentArtwork, thumbnail);
                    this.Artwork = thumbnail;
                    thumbnailPublished = thumbnail is not null;
                }

                this._artworkLoadCompleted = true;
                this._artworkCancellation?.Dispose();
                this._artworkCancellation = null;
            }

            if (presentationChanged)
            {
                this.RaiseChanged();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var presentationChanged = false;
            lock (this._artworkLock)
            {
                if (this._artworkKey == artworkKey)
                {
                    presentationChanged = this.Artwork is not null;
                    this.Artwork = null;
                    this._artworkLoadCompleted = true;
                    this._artworkCancellation?.Dispose();
                    this._artworkCancellation = null;
                }
            }

            Logger.LogError("Failed to load media artwork.", ex);
            if (presentationChanged)
            {
                this.RaiseChanged();
            }
        }
        finally
        {
            if (!thumbnailPublished)
            {
                thumbnail?.DisposeUnpublished();
            }
        }
    }

    private void CancelArtworkLoadUnderLock()
    {
        this._artworkCancellation?.Cancel();
        this._artworkCancellation?.Dispose();
        this._artworkCancellation = null;
    }

    private void RaiseChanged()
    {
        if (!this._disposed)
        {
            DiagnosticEvent.Raise(
                this,
                this.Changed,
                $"MediaSessionViewModel[{this.Session.Id.Value}].Changed");
        }
    }

    private static IAppInfo ResolveAppInfo(string applicationId)
    {
        if (string.IsNullOrWhiteSpace(applicationId))
        {
            return EmptyAppInfo.Instance;
        }

        var appInfo = ModernAppHelper.Get(applicationId);
        if (appInfo?.DisplayInfo is not null)
        {
            return new ModernAppInfo(
                appInfo,
                PackageIconHelper.GetBestIconPath(applicationId));
        }

        return (IAppInfo?)DesktopAppHelper.GetExecutable(applicationId)
            ?? EmptyAppInfo.Instance;
    }
}
