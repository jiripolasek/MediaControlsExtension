// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Pages;

internal sealed partial class MediaMetadataPageCache : IDisposable
{
#if FF_ENABLE_FULL_METADATA_PAGE
    private readonly Lock _stateLock = new();
    private readonly IMediaService _mediaService;
    private readonly MediaSessionViewModelCache _viewModels;
    private readonly MediaCommandResultFactory _resultFactory;
    private readonly IIconService _iconService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Dictionary<MediaSessionId, MediaMetadataPage> _pages = [];
    private bool _disposed;
#endif

    public MediaMetadataPageCache(
        IMediaService mediaService,
        MediaSessionViewModelCache viewModels,
        MediaCommandResultFactory resultFactory,
        IIconService iconService,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(viewModels);
        ArgumentNullException.ThrowIfNull(resultFactory);
        ArgumentNullException.ThrowIfNull(iconService);
        ArgumentNullException.ThrowIfNull(loggerFactory);

#if FF_ENABLE_FULL_METADATA_PAGE
        this._mediaService = mediaService;
        this._viewModels = viewModels;
        this._resultFactory = resultFactory;
        this._iconService = iconService;
        this._loggerFactory = loggerFactory;
        this._mediaService.SessionsChanged += this.MediaServiceOnSessionsChanged;
#endif
    }

#if FF_ENABLE_FULL_METADATA_PAGE
    public MediaMetadataPage GetOrCreate(MediaSessionViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        lock (this._stateLock)
        {
            ObjectDisposedException.ThrowIf(this._disposed, this);
            var sessionId = viewModel.Session.Id;
            if (this._pages.TryGetValue(sessionId, out var page))
            {
                return page;
            }

            page = MediaMetadataPage.ForSession(
                this._mediaService,
                this._viewModels,
                viewModel,
                this._resultFactory,
                this._iconService,
                this._loggerFactory);
            this._pages.Add(sessionId, page);
            return page;
        }
    }

    private void MediaServiceOnSessionsChanged(object? sender, EventArgs args)
    {
        var liveIds = this._mediaService.Sessions
            .Select(static session => session.Id)
            .ToHashSet();

        lock (this._stateLock)
        {
            if (this._disposed)
            {
                return;
            }

            foreach (var sessionId in this._pages.Keys
                         .Where(sessionId => !liveIds.Contains(sessionId))
                         .ToArray())
            {
                this._pages.Remove(sessionId);
            }
        }
    }
#endif

    public void Dispose()
    {
#if FF_ENABLE_FULL_METADATA_PAGE
        lock (this._stateLock)
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            this._pages.Clear();
        }

        this._mediaService.SessionsChanged -= this.MediaServiceOnSessionsChanged;
#endif
    }
}
