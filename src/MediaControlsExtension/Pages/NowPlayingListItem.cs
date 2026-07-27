// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Pages;

internal sealed partial class NowPlayingListItem : ListItemBase, IDisposable
{
    private readonly IMediaService _mediaService;
    private readonly MediaSessionViewModelCache _viewModels;
#if FF_ENABLE_FULL_METADATA_PAGE
    private readonly MediaMetadataPageCache _metadataPages;
#endif
    private readonly SettingsManager _settingsManager;
    private readonly IIconService _iconService;
    private readonly IconSurface _iconSurface;
    private readonly ThrottledAction _updateMediaInfo;

    private readonly Lock _currentSessionLock = new();
    private readonly Lock _updateLock = new();
    private readonly OptimisticPlaybackCommand _playPauseCommand;
    private readonly BringAssociatedAppToFrontCommand _switchToApplicationCommand;
    private readonly CurrentSessionCommand _nextTrackCommand;
    private readonly CurrentSessionCommand _previousTrackCommand;
#if FF_ENABLE_FULL_METADATA_PAGE
    private MediaMetadataPage? _metadataPage;
#endif
    private readonly IContextItem[] _mediaContextCommandsWithoutMetadata;
    private IContextItem[] _mediaContextCommands;
    private readonly bool _isBandPage;

    private MediaSessionViewModel? _currentSession;
    private MediaDetails? _mediaDetails;
    private int _detailsRequested;
    private int _disposed;

    internal event EventHandler? DetailsChanged;

    internal CurrentSessionCommand NextTrackCommand => this._nextTrackCommand;

    internal CurrentSessionCommand PreviousTrackCommand => this._previousTrackCommand;

    public override IDetails? Details
    {
        get
        {
            Interlocked.Exchange(ref this._detailsRequested, 1);
            var viewModel = Volatile.Read(ref this._currentSession);
            if (Volatile.Read(ref this._disposed) != 0 ||
                viewModel is not { IsAvailable: true })
            {
                return null;
            }

            var details = Volatile.Read(ref this._mediaDetails);
            if (details is null || !details.Represents(viewModel))
            {
                viewModel.RequestArtwork();
                var newDetails = this.CreateDetails(viewModel);
                var publishedDetails = Interlocked.CompareExchange(
                    ref this._mediaDetails,
                    newDetails,
                    details);
                details = ReferenceEquals(publishedDetails, details)
                    ? newDetails
                    : publishedDetails;
            }

            if (!ReferenceEquals(viewModel, Volatile.Read(ref this._currentSession)))
            {
                return null;
            }

            return details?.Details;
        }
        set
        {
        }
    }

    public NowPlayingListItem(
        IMediaService mediaService,
        MediaSessionViewModelCache viewModels,
        MediaMetadataPageCache metadataPages,
        SettingsManager settingsManager,
        MediaCommandResultFactory resultFactory,
        IIconService iconService,
        bool asBandPage) : base(new NoOpCommand())
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(viewModels);
        ArgumentNullException.ThrowIfNull(metadataPages);
        ArgumentNullException.ThrowIfNull(settingsManager);
        ArgumentNullException.ThrowIfNull(iconService);

        this._isBandPage = asBandPage;
        this._iconSurface = asBandPage
            ? IconSurface.Dock
            : IconSurface.CommandPalette;
        this._mediaService = mediaService;
        this._viewModels = viewModels;
#if FF_ENABLE_FULL_METADATA_PAGE
        this._metadataPages = metadataPages;
#endif
        this._settingsManager = settingsManager;
        this._iconService = iconService;
        this._updateMediaInfo = new(
            150,
            asBandPage ? "NowPlayingDock.Update" : "NowPlayingListItem.Update",
            this.UpdateCurrentSession);

        this._switchToApplicationCommand = new(this._mediaService, this._viewModels);
        this.Command = this._playPauseCommand = new(
            this._mediaService,
            resultFactory,
            this._iconService,
            this._iconSurface)
        {
            Id = "com.jpsoftworks.cmdpal.mediacontrols.nowplaying",
            Icon = this._iconService.GetIcon(ThemedIcon.NoMedia, this._iconSurface)
        };
        this._nextTrackCommand = new CurrentSessionCommand(this._mediaService, MediaSessionOperations.SkipNextTrack, resultFactory) { Name = Strings.Command_NextTrack, Icon = this._iconService.GetIcon(ThemedIcon.SkipNext, this._iconSurface) };
        this._previousTrackCommand = new CurrentSessionCommand(this._mediaService, MediaSessionOperations.SkipPreviousTrack, resultFactory) { Name = Strings.Command_PreviousTrack, Icon = this._iconService.GetIcon(ThemedIcon.SkipPrevious, this._iconSurface) };
        var toggleRepeatCommand = new CurrentSessionCommand(this._mediaService, MediaSessionOperations.ToggleRepeat, resultFactory) { Name = Strings.Command_ToggleRepeat };
        var toggleShuffleCommand = new CurrentSessionCommand(this._mediaService, MediaSessionOperations.ToggleShuffle, resultFactory) { Name = Strings.Command_ToggleShuffle };
        this._mediaContextCommandsWithoutMetadata = [
            new CommandContextItem(this._switchToApplicationCommand) { RequestedShortcut = Chords.SwitchToApplication, Icon = Icons.SwitchApps },
            new CommandContextItem(this._nextTrackCommand) { RequestedShortcut = Chords.NextTrack, Icon = Icons.NextTrackOutline},
            new CommandContextItem(this._previousTrackCommand) { RequestedShortcut = Chords.PreviousTrack, Icon = Icons.PreviousTrackOutline},
            new CommandContextItem(toggleRepeatCommand) { RequestedShortcut = Chords.ToggleRepeat, Icon = Icons.ToggleRepeat},
            new CommandContextItem(toggleShuffleCommand) { RequestedShortcut = Chords.ToggleShuffle, Icon = Icons.ToggleShuffle},

            new CommandContextItem(new CurrentSessionCommand(this._mediaService, new PlayNextSessionMop(this._viewModels), resultFactory) { Name = Strings.Command_NextApp })  { RequestedShortcut = Chords.NextSession, Icon = Icons.NextApp },
            new CommandContextItem(new CurrentSessionCommand(this._mediaService, new PlayPreviousSessionMop(this._viewModels), resultFactory) { Name = Strings.Command_PreviousApp })  { RequestedShortcut = Chords.PreviousSession, Icon = Icons.PreviousApp },
        ];
        this._mediaContextCommands = this._mediaContextCommandsWithoutMetadata;

        this.Title = this._isBandPage ? string.Empty : Strings.Command_PlayPause!;
        this.UpdateIcon(this._iconService.GetIcon(ThemedIcon.PlayPause, this._iconSurface));

        // Subscribe first, then seed by re-reading inside the lock: a handler that
        // ran in between installed a value the service already held, so the locked
        // re-read returns that value or a newer one and cannot resurrect a stale
        // session. The seed also wires the per-session event and schedules the
        // initial update.
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
            if (Volatile.Read(ref this._disposed) == 0)
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
        if (Volatile.Read(ref this._disposed) != 0)
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

        Volatile.Write(ref this._currentSession, viewModel);
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
            if (Volatile.Read(ref this._disposed) != 0)
            {
                return;
            }

            currentSession = this._currentSession;
        }

        this.Update(currentSession);
    }

    private void Update(MediaSessionViewModel? viewModel)
    {
        var detailsChanged = false;
        lock (this._updateLock)
        {
            if (Volatile.Read(ref this._disposed) != 0)
            {
                return;
            }

            if (viewModel is not { IsAvailable: true })
            {
                this.Title = this._isBandPage ? string.Empty : Strings.NowPlaying_NothingPlaying!;
                this.Icon = this._iconService.GetIcon(ThemedIcon.NoMedia, this._iconSurface);
                this.Subtitle = string.Empty;

                this._playPauseCommand.UpdatePresentation(null, showName: !this._isBandPage);
#if FF_ENABLE_FULL_METADATA_PAGE
                this.UpdateMetadataPage(null);
#endif
                detailsChanged = this.UpdateDetails(null);

                this.MoreCommands = [];
            }
            else
            {
                this.UpdateNavigationCommandIcons();
                var playbackAction = this._playPauseCommand.UpdatePresentation(
                    viewModel.Session,
                    showName: !this._isBandPage);
                var properties = viewModel.MediaProperties;

                this.Title = this._isBandPage
                    ? string.Empty
                    : playbackAction.CommandName;
                this.Subtitle = this._isBandPage
                    ? string.Empty
                    : StringHelper.JoinNonEmpty(
                        " • ",
                        (string?[])
                        [
                            properties.Title,
                            properties.Artist,
                            viewModel.ApplicationName,
                        ]);

                this.UpdateIcon(playbackAction.CommandIcon);
#if FF_ENABLE_FULL_METADATA_PAGE
                this.UpdateMetadataPage(viewModel);
#endif
                detailsChanged = this.UpdateDetails(viewModel);

                this.MoreCommands = this._mediaContextCommands;
            }
        }

        if (detailsChanged)
        {
            this.OnPropertyChanged(nameof(this.Details));
            this.DetailsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool UpdateDetails(MediaSessionViewModel? viewModel)
    {
        if (Volatile.Read(ref this._detailsRequested) == 0)
        {
            return false;
        }

        if (viewModel is not { IsAvailable: true })
        {
            return Interlocked.Exchange(ref this._mediaDetails, null) is not null;
        }

        viewModel.RequestArtwork();
        var details = Volatile.Read(ref this._mediaDetails);
        if (details is not null && details.Represents(viewModel))
        {
            return false;
        }

        Volatile.Write(ref this._mediaDetails, this.CreateDetails(viewModel));
        return true;
    }

    private MediaDetails CreateDetails(MediaSessionViewModel viewModel)
    {
        ICommand? viewMetadataCommand = null;
#if FF_ENABLE_FULL_METADATA_PAGE
        viewMetadataCommand = this._metadataPages.GetOrCreate(viewModel);
#endif
        return new(
            this._previousTrackCommand,
            this._playPauseCommand,
            this._nextTrackCommand,
            this._switchToApplicationCommand,
            viewModel,
            viewMetadataCommand);
    }

#if FF_ENABLE_FULL_METADATA_PAGE
    private void UpdateMetadataPage(MediaSessionViewModel? viewModel)
    {
        var metadataPage = viewModel is not null
            ? this._metadataPages.GetOrCreate(viewModel)
            : null;
        if (ReferenceEquals(this._metadataPage, metadataPage))
        {
            return;
        }

        this._metadataPage = metadataPage;
        if (metadataPage is null)
        {
            this._mediaContextCommands = this._mediaContextCommandsWithoutMetadata;
            return;
        }

        var commands = new IContextItem[this._mediaContextCommandsWithoutMetadata.Length + 1];
        commands[0] = this._mediaContextCommandsWithoutMetadata[0];
        commands[1] = new CommandContextItem(metadataPage) { Icon = Icons.Metadata };
        Array.Copy(
            this._mediaContextCommandsWithoutMetadata,
            1,
            commands,
            2,
            this._mediaContextCommandsWithoutMetadata.Length - 1);
        this._mediaContextCommands = commands;
    }
#endif

    private void SettingsOnSettingsChanged(object sender, Settings args)
    {
        this.ScheduleUpdate();
    }

    private void UpdateNavigationCommandIcons()
    {
        if (this._nextTrackCommand is Command nextTrackCommand)
        {
            nextTrackCommand.UpdateIcon(this._iconService.GetIcon(
                ThemedIcon.SkipNext,
                this._iconSurface));
        }

        if (this._previousTrackCommand is Command previousTrackCommand)
        {
            previousTrackCommand.UpdateIcon(this._iconService.GetIcon(
                ThemedIcon.SkipPrevious,
                this._iconSurface));
        }
    }

    private void ScheduleUpdate()
    {
        lock (this._currentSessionLock)
        {
            if (Volatile.Read(ref this._disposed) == 0)
            {
                this._updateMediaInfo.Invoke();
            }
        }
    }

    private void CurrentSessionOnChanged(object? sender, EventArgs args)
    {
        lock (this._currentSessionLock)
        {
            if (Volatile.Read(ref this._disposed) == 0 &&
                ReferenceEquals(sender, this._currentSession))
            {
                this._updateMediaInfo.Invoke();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref this._disposed, 1) != 0)
        {
            return;
        }

        MediaSessionViewModel? currentSession;
        lock (this._currentSessionLock)
        {
            currentSession = this._currentSession;
            Volatile.Write(ref this._currentSession, null);
        }

        this._updateMediaInfo.Dispose();
        this._settingsManager.Settings.SettingsChanged -= this.SettingsOnSettingsChanged;
        this._mediaService.CurrentSessionChanged -= this.MediaServiceOnCurrentSessionChanged;
        if (currentSession is not null)
        {
            currentSession.Changed -= this.CurrentSessionOnChanged;
        }

        this._playPauseCommand.UpdatePresentation(null);
        Interlocked.Exchange(ref this._mediaDetails, null);
        this.DetailsChanged = null;
        this.MoreCommands = [];
        this._mediaContextCommands = [];
#if FF_ENABLE_FULL_METADATA_PAGE
        Interlocked.Exchange(ref this._metadataPage, null);
#endif
    }
}
