// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Pages;

internal sealed partial class MediaControlsExtensionPage : ListPage, IDisposable
{
    private readonly SettingsManager _settingsManager;
    private readonly MediaCommandResultFactory _resultFactory;
    private readonly IMediaService _mediaService;
    private readonly MediaSessionViewModelCache _viewModels;
    private readonly MediaMetadataPageCache _metadataPages;
    private readonly IIconService _iconService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IconSurface _iconSurface;
    private readonly Lock _refreshLock = new();
    private readonly bool _isBandPage;
    private readonly Separator _playbackSectionSeparator = new(Strings.Page_Section_Playback!);
    private readonly Separator _systemVolumeSectionSeparator = new(Strings.Page_Section_SystemVolume!);
    private readonly Separator _mediaSessionsSectionSeparator = new(Strings.Page_Section_MediaSessions!);

    private bool _isInitialized;
    private bool _disposed;
    private MediaSession? _currentSession;
    private readonly NowPlayingListItem _playPauseCurrentSessionItem;
    private readonly DockHeadItem? _bandFirstItem;
    private readonly DetailsForwardingListItem _nextTrackCurrentSessionItem;
    private readonly DetailsForwardingListItem _prevTrackCurrentSessionItem;
    private readonly VolumeListItem? _volumeItem;
    private List<MediaSessionListItem> _items = [];
    private IListItem[] _cachedItems = [];

    public MediaControlsExtensionPage(
        IMediaService mediaService,
        MediaSessionViewModelCache viewModels,
        MediaMetadataPageCache metadataPages,
        SystemVolumeService systemVolumeService,
        SettingsManager settingsManager,
        MediaCommandResultFactory resultFactory,
        IIconService iconService,
        ILoggerFactory loggerFactory,
        DockHeadCommandTargets? dockHeadCommandTargets = null)
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(viewModels);
        ArgumentNullException.ThrowIfNull(metadataPages);
        ArgumentNullException.ThrowIfNull(systemVolumeService);
        ArgumentNullException.ThrowIfNull(settingsManager);
        ArgumentNullException.ThrowIfNull(resultFactory);
        ArgumentNullException.ThrowIfNull(iconService);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        this._isBandPage = dockHeadCommandTargets is not null;
        this._iconSurface = this._isBandPage
            ? IconSurface.Dock
            : IconSurface.CommandPalette;
        this._settingsManager = settingsManager;
        this._resultFactory = resultFactory;
        this._mediaService = mediaService;
        this._viewModels = viewModels;
        this._metadataPages = metadataPages;
        this._iconService = iconService;
        this._loggerFactory = loggerFactory;
        this._settingsManager.Settings.SettingsChanged += this.SettingsOnSettingsChanged;

        this.Icon = Icons.MainIcon;
        this.Title = Strings.Name!;
        this.Name = Strings.Open!;
        this.Id = "com.jpsoftworks.cmdpal.mediacontrols";
        this.PlaceholderText = Strings.SearchPlaceholder!;
        this.ShowDetails = !this._isBandPage && this._settingsManager.ShowDetails;

        this._mediaService.StatusChanged += this.MediaServiceOnStatusChanged;
        this._mediaService.SessionsChanged += this.MediaServiceOnSessionsChanged;
        this._mediaService.CurrentSessionChanged += this.MediaServiceOnCurrentSessionChanged;

        this.EmptyContent = new CommandItem
        {
            Title = Strings.EmptyContent_Title!,
            Subtitle = Strings.EmptyContent_Subtitle!,
            Icon = Icons.MainIcon
        };

        this._playPauseCurrentSessionItem = new NowPlayingListItem(
            this._mediaService,
            this._viewModels,
            this._metadataPages,
            this._settingsManager,
            this._resultFactory,
            this._iconService,
            this._loggerFactory,
            this._isBandPage);
        this._bandFirstItem = dockHeadCommandTargets is not null
            ? new DockHeadItem(
                this._mediaService,
                this._viewModels,
                this._settingsManager,
                this._resultFactory,
                this._iconService,
                this._loggerFactory,
                dockHeadCommandTargets)
            : null;

        // Do not reuse the named track commands from the now-playing item here.
        // ListItem.Title falls back to Command.Name when set to an empty string,
        // which would make the skip-track labels reappear in the dock.
        this._nextTrackCurrentSessionItem = new(
            new CurrentSessionCommand(
                this._mediaService,
                MediaSessionOperations.SkipNextTrack,
                this._resultFactory,
                this._loggerFactory),
            this._playPauseCurrentSessionItem)
        {
            Title = Strings.Command_NextTrack,
            // Subtitle = Strings.Command_NextTrack_Subtitle
        };
        this._prevTrackCurrentSessionItem = new(
            new CurrentSessionCommand(
                this._mediaService,
                MediaSessionOperations.SkipPreviousTrack,
                this._resultFactory,
                this._loggerFactory),
            this._playPauseCurrentSessionItem)
        {
            Title = Strings.Command_PreviousTrack,
            // Subtitle = Strings.Command_PreviousTrack_Subtitle
        };
        this.UpdateTrackNavigationIcons();
        this._volumeItem = this._isBandPage
            ? null
            : new(
                systemVolumeService,
                this._resultFactory,
                this._iconService,
                this._iconSurface,
                VolumeListItemPresentation.Page,
                this._loggerFactory);

        if (this._isBandPage)
        {
            this._playPauseCurrentSessionItem.Title = string.Empty;
            this._playPauseCurrentSessionItem.Subtitle = string.Empty;
            this._nextTrackCurrentSessionItem.Title = string.Empty;
            this._nextTrackCurrentSessionItem.Subtitle = string.Empty;
            this._prevTrackCurrentSessionItem.Title = string.Empty;
            this._prevTrackCurrentSessionItem.Subtitle = string.Empty;
        }

        this.UpdateStatus();
        this.RebuildSessionItems();
        this.SetCurrentSession(this._mediaService.CurrentSession);
        this.RebuildAndRaiseIfChanged();
    }

    private void MediaServiceOnStatusChanged(object? sender, EventArgs args)
    {
        this.UpdateStatus();
        this.RebuildAndRaiseIfChanged();
    }

    private void MediaServiceOnSessionsChanged(object? sender, EventArgs args)
    {
        this.RebuildSessionItems();
    }

    private void MediaServiceOnCurrentSessionChanged(object? sender, EventArgs args)
    {
        this.SetCurrentSession(this._mediaService.CurrentSession);
        this.UpdateCurrentMediaItems();
    }

    private void CurrentSessionOnChanged(
        object? sender,
        MediaSessionChangedEventArgs args)
    {
        if ((args.Changes & (MediaSessionChanges.PlaybackInfo | MediaSessionChanges.Availability)) != 0)
        {
            this.UpdateCurrentMediaItems();
        }
    }

    private void SetCurrentSession(MediaSession? session)
    {
        lock (this._refreshLock)
        {
            if (this._disposed && session is not null)
            {
                return;
            }

            if (ReferenceEquals(this._currentSession, session))
            {
                return;
            }

            if (this._currentSession is not null)
            {
                this._currentSession.Changed -= this.CurrentSessionOnChanged;
            }

            this._currentSession = session;
            if (session is not null)
            {
                session.Changed += this.CurrentSessionOnChanged;
            }
        }
    }

    private void UpdateStatus()
    {
        var status = this._mediaService.Status;
        this._isInitialized = status is not MediaServiceStatus.Stopped and not MediaServiceStatus.Starting;
        this.IsLoading = status is MediaServiceStatus.Stopped or MediaServiceStatus.Starting;
    }

    private void RebuildSessionItems()
    {
        MediaSessionListItem[] removedItems;
        lock (this._refreshLock)
        {
            if (this._disposed)
            {
                return;
            }

            var existingItems = this._items.ToDictionary(
                static item => item.SessionId);
            var newItems = new List<MediaSessionListItem>();
            foreach (var session in this._mediaService.Sessions)
            {
                if (existingItems.Remove(session.Id, out var existingItem))
                {
                    newItems.Add(existingItem);
                }
                else
                {
                    newItems.Add(new(
                        this._mediaService,
                        this._viewModels,
                        this._metadataPages,
                        this._viewModels.GetOrCreate(session),
                        this._settingsManager,
                        this._resultFactory,
                        this._iconService,
                        this._loggerFactory,
                        this._isBandPage));
                }
            }

            removedItems = [.. existingItems.Values];
            this._items = newItems;
        }

        this.RebuildAndRaiseIfChanged();
        foreach (var item in removedItems)
        {
            item.Dispose();
        }
    }

    private void UpdateCurrentMediaItems()
    {
        lock (this._refreshLock)
        {
            if (this._disposed)
            {
                return;
            }
        }

        if (this._nextTrackCurrentSessionItem?.Command is CurrentSessionCommand nextTrackCommand)
        {
            this._nextTrackCurrentSessionItem.UpdateIcon(this._iconService.GetIcon(
                ThemedIcon.SkipNext,
                this._iconSurface,
                nextTrackCommand.CanExecute() ? IconState.Default : IconState.Disabled));
        }
        if (this._prevTrackCurrentSessionItem?.Command is CurrentSessionCommand prevTrackCommand)
        {
            this._prevTrackCurrentSessionItem.UpdateIcon(this._iconService.GetIcon(
                ThemedIcon.SkipPrevious,
                this._iconSurface,
                prevTrackCommand.CanExecute() ? IconState.Default : IconState.Disabled));
        }

        this.RebuildAndRaiseIfChanged();
    }

    private void SettingsOnSettingsChanged(object sender, Settings args)
    {
        this.ShowDetails = !this._isBandPage && this._settingsManager.ShowDetails;
        this.UpdateCurrentMediaItems();
    }

    private void UpdateTrackNavigationIcons()
    {
        this._nextTrackCurrentSessionItem.UpdateIcon(this._iconService.GetIcon(
            ThemedIcon.SkipNext,
            this._iconSurface));
        this._prevTrackCurrentSessionItem.UpdateIcon(this._iconService.GetIcon(
            ThemedIcon.SkipPrevious,
            this._iconSurface));
    }

    /// <summary>
    /// Rebuilds the items list and raises <see cref="RaiseItemsChanged"/> only when
    /// the item composition (identity or order) actually changed.
    /// </summary>
    private void RebuildAndRaiseIfChanged()
    {
        lock (this._refreshLock)
        {
            if (this._disposed)
            {
                return;
            }

            var newItems = this.BuildItems();
            if (ItemsEqual(this._cachedItems, newItems))
            {
                return;
            }

            this._cachedItems = newItems;
        }

        this.RaiseItemsChanged();
    }

    private IListItem[] BuildItems()
    {
        if (this._isBandPage)
        {
            return [.. this.GetBandItems()];
        }

        if (!this._isInitialized)
        {
            this.IsLoading = true;
            return [.. this.GetGlobalCommands()];
        }

        var items = this.GetGlobalCommands();
        var sectionStart = items.Count;
        items.AddRange(this._items);
        InsertSectionSeparatorIfNotEmpty(
            items,
            sectionStart,
            this._mediaSessionsSectionSeparator);
        return [.. items];
    }

    public override IListItem[] GetItems()
    {
        lock (this._refreshLock)
        {
            return this._cachedItems;
        }
    }

    private List<IListItem> GetGlobalCommands()
    {
        List<IListItem> items = [];

        var sectionStart = items.Count;
        items.Add(this._playPauseCurrentSessionItem);
        if (this._settingsManager.ShowSkipCommands)
        {
            items.Add(this._nextTrackCurrentSessionItem);
            items.Add(this._prevTrackCurrentSessionItem);
        }

        InsertSectionSeparatorIfNotEmpty(
            items,
            sectionStart,
            this._playbackSectionSeparator);

        sectionStart = items.Count;
        if (this._settingsManager.EnableVolumeControls && this._volumeItem is not null)
        {
            items.Add(this._volumeItem);
        }

        InsertSectionSeparatorIfNotEmpty(
            items,
            sectionStart,
            this._systemVolumeSectionSeparator);
        return items;
    }

    private static void InsertSectionSeparatorIfNotEmpty(
        List<IListItem> items,
        int sectionStart,
        Separator separator)
    {
        if (items.Count > sectionStart)
        {
            items.Insert(sectionStart, separator);
        }
    }

    private List<IListItem> GetBandItems()
    {
        if (!this._isBandPage || this._bandFirstItem is null)
        {
            return [];
        }

        List<IListItem> items = [];

        items.Add(this._bandFirstItem!);

        if (this._mediaService.CurrentSession is not null)
        {
            if (this._settingsManager.ShowSkipCommandsInDockBand)
            {
                items.Add(this._prevTrackCurrentSessionItem!);
            }
            items.Add(this._playPauseCurrentSessionItem);
            if (this._settingsManager.ShowSkipCommandsInDockBand)
            {
                items.Add(this._nextTrackCurrentSessionItem!);
            }
        }
        return items;
    }

    private static bool ItemsEqual(IListItem[] a, IListItem[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (!ReferenceEquals(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        MediaSessionListItem[] items;
        lock (this._refreshLock)
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            items = [.. this._items];
            this._items.Clear();
            this._cachedItems = [];
        }

        this._mediaService.StatusChanged -= this.MediaServiceOnStatusChanged;
        this._mediaService.SessionsChanged -= this.MediaServiceOnSessionsChanged;
        this._mediaService.CurrentSessionChanged -= this.MediaServiceOnCurrentSessionChanged;
        this.SetCurrentSession(null);
        this._settingsManager.Settings.SettingsChanged -= this.SettingsOnSettingsChanged;
        this._nextTrackCurrentSessionItem?.Dispose();
        this._prevTrackCurrentSessionItem?.Dispose();
        this._playPauseCurrentSessionItem?.Dispose();
        this._bandFirstItem?.Dispose();
        this._volumeItem?.Dispose();
        foreach (var item in items)
        {
            item.Dispose();
        }
    }

    internal VolumeListItem? VolumeItem => this._volumeItem;
}
