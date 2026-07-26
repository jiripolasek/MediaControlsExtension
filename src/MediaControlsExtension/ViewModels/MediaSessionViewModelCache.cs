// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.ViewModels;

/// <summary>
/// Keeps one presentation view model for each live keyed media session.
/// It does not mirror the service's session list or current-session state.
/// </summary>
internal sealed partial class MediaSessionViewModelCache : IDisposable
{
    private readonly IMediaService _mediaService;
    private readonly Lock _stateLock = new();
    private readonly Dictionary<MediaSessionId, MediaSessionViewModel> _viewModels = [];
    private bool _disposed;

    public MediaSessionViewModelCache(IMediaService mediaService)
    {
        this._mediaService = mediaService ?? throw new ArgumentNullException(nameof(mediaService));
        mediaService.SessionsChanged += this.MediaServiceOnSessionsChanged;
    }

    public MediaSessionViewModel GetOrCreate(MediaSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (this._stateLock)
        {
            ObjectDisposedException.ThrowIf(this._disposed, this);
            if (this._viewModels.TryGetValue(session.Id, out var viewModel))
            {
                return viewModel;
            }

            viewModel = new(this._mediaService, session);
            this._viewModels.Add(session.Id, viewModel);
            return viewModel;
        }
    }

    public void Dispose()
    {
        MediaSessionViewModel[] viewModels;
        lock (this._stateLock)
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            viewModels = [.. this._viewModels.Values];
            this._viewModels.Clear();
        }

        this._mediaService.SessionsChanged -= this.MediaServiceOnSessionsChanged;
        foreach (var viewModel in viewModels)
        {
            viewModel.Dispose();
        }
    }

    private void MediaServiceOnSessionsChanged(object? sender, EventArgs args)
    {
        var liveIds = this._mediaService.Sessions
            .Select(static session => session.Id)
            .ToHashSet();
        List<MediaSessionViewModel> removed = [];

        lock (this._stateLock)
        {
            if (this._disposed)
            {
                return;
            }

            foreach (var (id, viewModel) in this._viewModels)
            {
                if (!liveIds.Contains(id))
                {
                    removed.Add(viewModel);
                }
            }

            foreach (var viewModel in removed)
            {
                this._viewModels.Remove(viewModel.Session.Id);
            }
        }

        foreach (var viewModel in removed)
        {
            viewModel.Dispose();
        }
    }
}