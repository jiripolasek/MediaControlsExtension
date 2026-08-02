// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class CurrentSessionNavigationCommandItem : CommandItem, IDisposable
{
    private readonly Lock _stateLock = new();
    private readonly Lock _presentationLock = new();
    private readonly IMediaService _mediaService;
    private readonly MediaSessionViewModelCache _viewModels;
    private readonly StandaloneCurrentSessionCommand _command;
    private readonly IIconService _iconService;
    private readonly ThemedIcon _themedIcon;

    private MediaSessionViewModel? _currentSession;
    private bool _disposed;

    public CurrentSessionNavigationCommandItem(
        StandaloneCurrentSessionCommand command,
        IMediaService mediaService,
        MediaSessionViewModelCache viewModels,
        IIconService iconService,
        ThemedIcon themedIcon)
        : base(command)
    {
        this._command = command ?? throw new ArgumentNullException(nameof(command));
        this._mediaService = mediaService ?? throw new ArgumentNullException(nameof(mediaService));
        this._viewModels = viewModels ?? throw new ArgumentNullException(nameof(viewModels));
        this._iconService = iconService ?? throw new ArgumentNullException(nameof(iconService));
        this._themedIcon = themedIcon;

        this._mediaService.CurrentSessionChanged += this.MediaServiceOnCurrentSessionChanged;
        this._iconService.IconsChanged += this.IconServiceOnIconsChanged;
        lock (this._stateLock)
        {
            this.SetCurrentSessionUnderLock(this.ResolveCurrentSession());
        }

        this.UpdatePresentation();
    }

    private MediaSessionViewModel? ResolveCurrentSession() =>
        this._mediaService.CurrentSession is { } session
            ? this._viewModels.GetOrCreate(session)
            : null;

    private void MediaServiceOnCurrentSessionChanged(object? sender, EventArgs args)
    {
        lock (this._stateLock)
        {
            if (this._disposed)
            {
                return;
            }

            this.SetCurrentSessionUnderLock(this.ResolveCurrentSession());
        }

        this.UpdatePresentation();
    }

    private void SetCurrentSessionUnderLock(MediaSessionViewModel? viewModel)
    {
        if (ReferenceEquals(this._currentSession, viewModel))
        {
            return;
        }

        if (this._currentSession is not null)
        {
            this._currentSession.Changed -= this.CurrentSessionOnChanged;
        }

        this._currentSession = viewModel;
        if (viewModel is not null)
        {
            viewModel.Changed += this.CurrentSessionOnChanged;
        }
    }

    private void CurrentSessionOnChanged(object? sender, EventArgs args)
    {
        lock (this._stateLock)
        {
            if (this._disposed || !ReferenceEquals(sender, this._currentSession))
            {
                return;
            }
        }

        this.UpdatePresentation();
    }

    private void IconServiceOnIconsChanged(object? sender, EventArgs args)
        => this.UpdatePresentation();

    private void UpdatePresentation()
    {
        lock (this._presentationLock)
        {
            MediaSessionViewModel? viewModel;
            lock (this._stateLock)
            {
                if (this._disposed)
                {
                    return;
                }

                viewModel = this._currentSession;
            }

            var isAvailable = viewModel is { IsAvailable: true };
            var isEnabled = isAvailable && this._command.MediaSessionOp.CanExecute(viewModel!.Session);
            var subtitle = CreateSubtitle(viewModel, isAvailable, isEnabled);
            var icon = this._iconService.GetIcon(
                this._themedIcon,
                IconSurface.CommandPalette,
                isEnabled ? IconState.Default : IconState.Disabled);

            lock (this._stateLock)
            {
                if (this._disposed || !ReferenceEquals(viewModel, this._currentSession))
                {
                    return;
                }

                if (!string.Equals(this.Subtitle, subtitle, StringComparison.Ordinal))
                {
                    this.Subtitle = subtitle;
                }

                this.UpdateIcon(icon);
                this._command.UpdateIcon(icon);
            }
        }
    }

    private static string CreateSubtitle(
        MediaSessionViewModel? viewModel,
        bool isAvailable,
        bool isEnabled)
    {
        if (!isAvailable)
        {
            return Strings.NowPlaying_NothingPlaying!;
        }

        var playerName = GetKnownPlayerName(viewModel!);
        if (isEnabled)
        {
            return playerName;
        }

        return string.IsNullOrEmpty(playerName)
            ? Strings.Details_NotAvailable!
            : $"{playerName} • {Strings.Details_NotAvailable}";
    }

    private static string GetKnownPlayerName(MediaSessionViewModel viewModel)
    {
        var playerName = viewModel.ApplicationName;
        return string.IsNullOrWhiteSpace(playerName) ||
               string.Equals(
                   playerName,
                   viewModel.MediaProperties.Application.ApplicationId,
                   StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : playerName;
    }

    public void Dispose()
    {
        MediaSessionViewModel? currentSession;
        lock (this._presentationLock)
        {
            lock (this._stateLock)
            {
                if (this._disposed)
                {
                    return;
                }

                this._disposed = true;
                currentSession = this._currentSession;
                this._currentSession = null;
            }
        }

        this._mediaService.CurrentSessionChanged -= this.MediaServiceOnCurrentSessionChanged;
        this._iconService.IconsChanged -= this.IconServiceOnIconsChanged;
        if (currentSession is not null)
        {
            currentSession.Changed -= this.CurrentSessionOnChanged;
        }
    }
}