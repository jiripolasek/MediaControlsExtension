// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Pages;

internal sealed partial class DockHeadItem : ListItemBase, IDisposable
{
    private readonly MediaService _mediaService;
    private readonly SettingsManager _settingsManager;
    private readonly IIconService _iconService;
    private readonly ThrottledAction _updateMediaInfo;

    private readonly Lock _currentMediaSourceLock = new();
    private readonly Lock _updateLock = new();
    private readonly IContextItem[] _mediaContextCommands;

    private readonly BringAssociatedAppToFrontCommand _primaryMediaCommand;
    private readonly NoOpCommand _noOpCommand = new();

    private MediaSource? _currentMediaSource;
    private NiceIconInfo? _lastIcon;
    private bool _disposed;

    public DockHeadItem(
        MediaService mediaService,
        SettingsManager settingsManager,
        YetAnotherHelper yetAnotherHelper,
        IIconService iconService) : base(new NoOpCommand())
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(settingsManager);
        ArgumentNullException.ThrowIfNull(iconService);

        this._mediaService = mediaService;
        this._settingsManager = settingsManager;
        this._iconService = iconService;
        this._updateMediaInfo = new(150, this.UpdateCurrentMediaSource);

        this._mediaContextCommands = [

            new Separator(),
            new CommandContextItem(new MediaCurrentSessionCommand(this._mediaService, MediaSessionOperations.SkipNextTrack, yetAnotherHelper) { Name = Strings.Command_NextTrack }) { RequestedShortcut = Chords.NextTrack, Icon = Icons.NextTrackOutline},
            new CommandContextItem(new MediaCurrentSessionCommand(this._mediaService, MediaSessionOperations.SkipPreviousTrack, yetAnotherHelper) { Name = Strings.Command_PreviousTrack }) { RequestedShortcut = Chords.PreviousTrack, Icon = Icons.PreviousTrackOutline},

            new Separator(),
            new CommandContextItem(new MediaCurrentSessionCommand(this._mediaService, MediaSessionOperations.ToggleRepeat, yetAnotherHelper) { Name = Strings.Command_ToggleRepeat }) { RequestedShortcut = Chords.ToggleRepeat, Icon = Icons.ToggleRepeat},
            new CommandContextItem(new MediaCurrentSessionCommand(this._mediaService, MediaSessionOperations.ToggleShuffle, yetAnotherHelper) { Name = Strings.Command_ToggleShuffle }) { RequestedShortcut = Chords.ToggleShuffle, Icon = Icons.ToggleShuffle},

            new Separator(),
            new CommandContextItem(new MediaCurrentSessionCommand(this._mediaService, new PlayNextSessionMop(this._settingsManager, this._mediaService), yetAnotherHelper) { Name = Strings.Command_NextApp })  { RequestedShortcut = Chords.NextSession, Icon = Icons.NextApp },
            new CommandContextItem(new MediaCurrentSessionCommand(this._mediaService, new PlayPreviousSessionMop(this._settingsManager, this._mediaService), yetAnotherHelper) { Name = Strings.Command_PreviousApp })  { RequestedShortcut = Chords.PreviousSession, Icon = Icons.PreviousApp },
        ];

        this._primaryMediaCommand = new BringAssociatedAppToFrontCommand(this._mediaService);
        this.Command = this._noOpCommand;

        this.Title = string.Empty;
        this.UpdateIcon(this._iconService.GetIcon(
            ThemedIcon.PlayPause,
            IconSurface.Dock));

        // Subscribe first, then seed by re-reading inside the lock; see
        // NowPlayingListItem for why the locked re-read cannot go stale.
        this._mediaService.CurrentMediaSourceChanged += this.CurrentMediaSourceChanged;
        this._settingsManager.Settings.SettingsChanged += this.SettingsOnSettingsChanged;
        lock (this._currentMediaSourceLock)
        {
            this.SetCurrentMediaSourceUnderLock(this._mediaService.CurrentSource);
        }
    }

    private void CurrentMediaSourceChanged(object? sender, MediaSource? arg)
    {
        lock (this._currentMediaSourceLock)
        {
            this.SetCurrentMediaSourceUnderLock(arg);
        }
    }

    private void SetCurrentMediaSourceUnderLock(MediaSource? mediaSource)
    {
        if (this._disposed)
        {
            return;
        }

        if (this._currentMediaSource != null)
        {
            this._currentMediaSource.PropChanged -= this.MediaSourceOnPropChanged;
        }

        this._currentMediaSource = mediaSource;

        if (this._currentMediaSource != null)
        {
            this._currentMediaSource.PropChanged += this.MediaSourceOnPropChanged;
        }

        this._updateMediaInfo.Invoke();
    }

    private void UpdateCurrentMediaSource()
    {
        lock (this._currentMediaSourceLock)
        {
            if (!this._disposed)
            {
                this.Update(this._currentMediaSource);
            }
        }
    }

    private void Update(MediaSource? mediaSource)
    {
        lock (this._updateLock)
        {
            if (this._disposed)
            {
                return;
            }

            if (mediaSource is not { HasProperties: true })
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
                this.Title = mediaSource.Name;
                this.Subtitle = StringHelper.JoinNonEmpty(" • ", mediaSource.Artist, mediaSource.ApplicationName);

                var iconBuildTask = BuildIcon(mediaSource, this._settingsManager.ShowThumbnails);
                if (this._lastIcon != iconBuildTask && iconBuildTask?.IconInfo != null)
                {
                    this._lastIcon = iconBuildTask;
                    this.UpdateIcon(iconBuildTask.IconInfo);
                }

                this.Command = this._primaryMediaCommand;
                this.MoreCommands = this._mediaContextCommands;
            }
        }

        return;

        static NiceIconInfo? BuildIcon(MediaSource mediaSource, bool showThumbnail)
        {
            if (showThumbnail && mediaSource.ThumbnailInfo?.Stream != null)
            {
                return new(mediaSource.ThumbnailInfo!);
            }

            if (mediaSource.ApplicationIconPath != null)
            {
                return new(mediaSource.ApplicationIconPath);
            }

            return null;
        }
    }

    private void SettingsOnSettingsChanged(object sender, Settings args)
    {
        this.ScheduleUpdate();
    }

    private void MediaSourceOnPropChanged(object sender, IPropChangedEventArgs args)
    {
        this.ScheduleUpdate();
    }

    private void ScheduleUpdate()
    {
        lock (this._currentMediaSourceLock)
        {
            if (!this._disposed)
            {
                this._updateMediaInfo?.Invoke();
            }
        }
    }

    public void Dispose()
    {
        MediaSource? currentMediaSource;
        lock (this._currentMediaSourceLock)
        {
            lock (this._updateLock)
            {
                if (this._disposed)
                {
                    return;
                }

                this._disposed = true;
                currentMediaSource = this._currentMediaSource;
                this._currentMediaSource = null;
            }
        }

        this._updateMediaInfo.Dispose();
        this._settingsManager.Settings.SettingsChanged -= this.SettingsOnSettingsChanged;
        this._mediaService.CurrentMediaSourceChanged -= this.CurrentMediaSourceChanged;
        if (currentMediaSource != null)
        {
            currentMediaSource.PropChanged -= this.MediaSourceOnPropChanged;
        }
    }
}