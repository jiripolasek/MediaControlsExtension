// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using Windows.Media;

namespace JPSoftworks.MediaControlsExtension.ViewModels;

/// <summary>
/// Adds CmdPal-specific source and artwork presentation to a live media session.
/// Media state and command behavior remain owned by <see cref="MediaSession"/>.
/// </summary>
internal sealed partial class MediaSessionViewModel : IDisposable
{
    private readonly IMediaService _mediaService;
    private readonly ILogger _logger;
    private readonly MediaSourcePresentation _sourcePresentation;
    private readonly Lock _artworkLock = new();

    private CancellationTokenSource? _artworkCancellation;
    private MediaArtworkKey? _artworkKey;
    private bool _artworkLoadCompleted;
    private bool _artworkRequested;
    private bool _disposed;

    public MediaSessionViewModel(
        IMediaService mediaService,
        MediaSession session,
        ILoggerFactory loggerFactory)
    {
        this._mediaService = mediaService ?? throw new ArgumentNullException(nameof(mediaService));
        this.Session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this._logger = loggerFactory.CreateLogger<MediaSessionViewModel>();
        this._sourcePresentation = new(
            static applicationId =>
            {
                var appInfo = AppInfoResolver.Resolve(applicationId);
                return Task.FromResult(new MediaApplicationPresentation(appInfo.DisplayName, appInfo.IconPath));
            },
            ex => ExtensionLog.UnexpectedError(this._logger, ex));
        this._sourcePresentation.Changed += this.SourcePresentationOnChanged;
        this._artworkKey = session.MediaProperties.Artwork;
        session.Changed += this.SessionOnChanged;
        _ = this._sourcePresentation.UpdateAsync(session.MediaProperties.Source);
    }

    public event EventHandler? Changed;

    public MediaSession Session { get; }

    public bool IsAvailable => this.Session.IsAvailable;

    public MediaPropertiesSnapshot MediaProperties => this.Session.MediaProperties;

    public MediaTimelinePropertiesSnapshot TimelineProperties => this.Session.TimelineProperties;

    public MediaPlaybackInfoSnapshot PlaybackInfo => this.Session.PlaybackInfo;

    public MediaSourcePresentationSnapshot SourcePresentation => this._sourcePresentation.State;

    public string SourceName => this.SourcePresentation.DisplayName;

    public string? SourceIconPath => this.SourcePresentation.IconPath;

    public ThumbnailInfo? Artwork { get; private set; }

    public MediaPlaybackType PlaybackType => this.MediaProperties.ContentType switch
    {
        MediaContentType.Music => MediaPlaybackType.Music,
        MediaContentType.Video => MediaPlaybackType.Video,
        MediaContentType.Image => MediaPlaybackType.Image,
        _ => MediaPlaybackType.Unknown,
    };

    public ValueTask<string> GetSourceNameAsync(CancellationToken cancellationToken = default) =>
        this._sourcePresentation.GetDisplayNameAsync(cancellationToken);

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

        this._sourcePresentation.Dispose();
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
            _ = this._sourcePresentation.UpdateAsync(properties.Source);
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

    private void SourcePresentationOnChanged(object? sender, EventArgs args)
    {
        _ = this._sourcePresentation.UpdateAsync(this.Session.MediaProperties.Source);
        this.RaiseChanged();
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

            ExtensionLog.Error(this._logger, "Failed to load media artwork.", ex);
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
                $"MediaSessionViewModel[{this.Session.Id.Value}].Changed",
                this._logger);
        }
    }
}