// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Pages;

internal sealed partial class DockHeadItem : ListItemBase, IDisposable
{
    private readonly IMediaService _mediaService;
    private readonly MediaSessionViewModelCache _viewModels;
    private readonly SettingsManager _settingsManager;
    private readonly IIconService _iconService;
    private readonly ThrottledAction _updateMediaInfo;

    private readonly Lock _currentSessionLock = new();
    private readonly Lock _updateLock = new();
    private readonly IContextItem[] _mediaContextCommands;

    private readonly BringAssociatedAppToFrontCommand _primaryMediaCommand;
    private readonly NoOpCommand _noOpCommand = new();

    private MediaSessionViewModel? _currentSession;
    private NiceIconInfo? _lastIcon;
    private bool _disposed;

    public DockHeadItem(
        IMediaService mediaService,
        MediaSessionViewModelCache viewModels,
        SettingsManager settingsManager,
        MediaCommandResultFactory resultFactory,
        IIconService iconService) : base(new NoOpCommand())
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(viewModels);
        ArgumentNullException.ThrowIfNull(settingsManager);
        ArgumentNullException.ThrowIfNull(iconService);

        this._mediaService = mediaService;
        this._viewModels = viewModels;
        this._settingsManager = settingsManager;
        this._iconService = iconService;
        this._updateMediaInfo = new(150, "DockHeadItem.Update", this.UpdateCurrentSession);

        this._mediaContextCommands = [

            new Separator(),
            new CommandContextItem(new CurrentSessionCommand(this._mediaService, MediaSessionOperations.SkipNextTrack, resultFactory) { Name = Strings.Command_NextTrack }) { RequestedShortcut = Chords.NextTrack, Icon = Icons.NextTrackOutline},
            new CommandContextItem(new CurrentSessionCommand(this._mediaService, MediaSessionOperations.SkipPreviousTrack, resultFactory) { Name = Strings.Command_PreviousTrack }) { RequestedShortcut = Chords.PreviousTrack, Icon = Icons.PreviousTrackOutline},

            new Separator(),
            new CommandContextItem(new CurrentSessionCommand(this._mediaService, MediaSessionOperations.ToggleRepeat, resultFactory) { Name = Strings.Command_ToggleRepeat }) { RequestedShortcut = Chords.ToggleRepeat, Icon = Icons.ToggleRepeat},
            new CommandContextItem(new CurrentSessionCommand(this._mediaService, MediaSessionOperations.ToggleShuffle, resultFactory) { Name = Strings.Command_ToggleShuffle }) { RequestedShortcut = Chords.ToggleShuffle, Icon = Icons.ToggleShuffle},

            new Separator(),
            new CommandContextItem(new CurrentSessionCommand(this._mediaService, new PlayNextSessionMop(this._viewModels), resultFactory) { Name = Strings.Command_NextApp })  { RequestedShortcut = Chords.NextSession, Icon = Icons.NextApp },
            new CommandContextItem(new CurrentSessionCommand(this._mediaService, new PlayPreviousSessionMop(this._viewModels), resultFactory) { Name = Strings.Command_PreviousApp })  { RequestedShortcut = Chords.PreviousSession, Icon = Icons.PreviousApp },
        ];

        this._primaryMediaCommand = new BringAssociatedAppToFrontCommand(
            this._mediaService,
            this._viewModels);
        this.Command = this._noOpCommand;

        this.Title = string.Empty;
        this.UpdateIcon(this._iconService.GetIcon(
            ThemedIcon.PlayPause,
            IconSurface.Dock));

        // Subscribe first, then seed by re-reading inside the lock; see
        // NowPlayingListItem for why the locked re-read cannot go stale.
        this._mediaService.CurrentSessionChanged += this.MediaServiceOnCurrentSessionChanged;
        this._settingsManager.Settings.SettingsChanged += this.SettingsOnSettingsChanged;
        lock (this._currentSessionLock)
        {
            this.SetCurrentSessionUnderLock(this.ResolveCurrentSession());
        }
    }

    private void MediaServiceOnCurrentSessionChanged(object? sender, EventArgs args)
    {
        lock (this._currentSessionLock)
        {
            if (!this._disposed)
            {
                this.SetCurrentSessionUnderLock(this.ResolveCurrentSession());
            }
        }
    }

    private MediaSessionViewModel? ResolveCurrentSession() =>
        this._mediaService.CurrentSession is { } session
            ? this._viewModels.GetOrCreate(session)
            : null;

    private void SetCurrentSessionUnderLock(MediaSessionViewModel? viewModel)
    {
        if (this._disposed)
        {
            return;
        }

        if (ReferenceEquals(this._currentSession, viewModel))
        {
            this._updateMediaInfo.Invoke();
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

        this._updateMediaInfo.Invoke();
    }

    private void UpdateCurrentSession()
    {
        MediaSessionViewModel? currentSession;
        lock (this._currentSessionLock)
        {
            if (this._disposed)
            {
                return;
            }

            currentSession = this._currentSession;
        }

        this.Update(currentSession);
    }

    private void Update(MediaSessionViewModel? viewModel)
    {
        lock (this._updateLock)
        {
            if (this._disposed)
            {
                return;
            }

            if (viewModel is not { IsAvailable: true })
            {
                this.Title = "";
                this.Subtitle = "";
                this.Icon = this._iconService.GetIcon(
                    ThemedIcon.NoMedia,
                    IconSurface.Dock);
                this._lastIcon = null;
                this.Command = this._noOpCommand;
                this.MoreCommands = [];

            }
            else
            {
                var properties = viewModel.MediaProperties;
                this.Title = properties.Title;
                this.Subtitle = StringHelper.JoinNonEmpty(
                    " • ",
                    (string?[])
                    [
                        properties.Artist,
                        viewModel.ApplicationName,
                    ]);

                if (this._settingsManager.ShowThumbnails)
                {
                    viewModel.RequestArtwork();
                }

                var icon = MediaSessionIcons.CreateDisplayIcon(
                    viewModel,
                    this._settingsManager.ShowThumbnails);
                if (this._lastIcon != icon && icon.IconInfo is not null)
                {
                    this._lastIcon = icon;
                    this.UpdateIcon(icon.IconInfo);
                }

                this.Command = this._primaryMediaCommand;
                this.MoreCommands = this._mediaContextCommands;
            }
        }
    }

    private void SettingsOnSettingsChanged(object sender, Settings args)
    {
        this.ScheduleUpdate();
    }

    private void CurrentSessionOnChanged(object? sender, EventArgs args)
    {
        lock (this._currentSessionLock)
        {
            if (!this._disposed && ReferenceEquals(sender, this._currentSession))
            {
                this._updateMediaInfo.Invoke();
            }
        }
    }

    private void ScheduleUpdate()
    {
        lock (this._currentSessionLock)
        {
            if (!this._disposed)
            {
                this._updateMediaInfo?.Invoke();
            }
        }
    }

    public void Dispose()
    {
        MediaSessionViewModel? currentSession;
        lock (this._currentSessionLock)
        {
            lock (this._updateLock)
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

        this._updateMediaInfo.Dispose();
        this._settingsManager.Settings.SettingsChanged -= this.SettingsOnSettingsChanged;
        this._mediaService.CurrentSessionChanged -= this.MediaServiceOnCurrentSessionChanged;
        if (currentSession is not null)
        {
            currentSession.Changed -= this.CurrentSessionOnChanged;
        }
    }
}
