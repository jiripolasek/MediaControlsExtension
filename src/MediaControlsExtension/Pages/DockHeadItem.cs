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
    private readonly ThrottledAction _updateMediaInfo;

    private readonly Lock _currentMediaSourceLock = new();
    private readonly Lock _updateLock = new();
    private readonly IContextItem[] _mediaContextCommands;

    private readonly BringAssociatedAppToFrontCommand _primaryMediaCommand;
    private readonly NoOpCommand _noOpCommand = new();

    private MediaSource? _currentMediaSource;
    private NiceIconInfo? _lastIcon;

    public DockHeadItem(MediaService mediaService, SettingsManager settingsManager, YetAnotherHelper yetAnotherHelper) : base(new NoOpCommand())
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(settingsManager);

        this._mediaService = mediaService;
        this._mediaService.CurrentMediaSourceChanged += this.CurrentMediaSourceChanged;

        this._settingsManager = settingsManager;
        this._settingsManager.Settings.SettingsChanged += this.SettingsOnSettingsChanged;

        this._currentMediaSource = this._mediaService.CurrentSource;
        this._updateMediaInfo = new(150, () => this.Update(this._currentMediaSource));

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
        this.UpdateIcon(Icons.PlayPause);

        this._updateMediaInfo.Invoke();
    }

    private void CurrentMediaSourceChanged(object? sender, MediaSource? arg)
    {
        lock (this._currentMediaSourceLock)
        {
            if (this._currentMediaSource != null)
            {
                this._currentMediaSource.PropChanged -= this.MediaSourceOnPropChanged;
            }

            this._currentMediaSource = arg;

            if (this._currentMediaSource != null)
            {
                this._currentMediaSource.PropChanged += this.MediaSourceOnPropChanged;
            }
        }

        this._updateMediaInfo?.Invoke();
    }

    private void Update(MediaSource? mediaSource)
    {
        lock (this._updateLock)
        {
            if (mediaSource is not { HasProperties: true })
            {
                this.Title = "";
                this.Subtitle = "";
                this.Icon = Icons.NoMedia;
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
        this._updateMediaInfo.Invoke();
    }

    private void MediaSourceOnPropChanged(object sender, IPropChangedEventArgs args)
    {
        this._updateMediaInfo.Invoke();
    }

    public override bool Equals(object? obj)
    {
        return ReferenceEquals(this, obj) || obj is NowPlayingListItem;
    }

    public override int GetHashCode()
    {
        return 0;
    }

    public void Dispose()
    {
        lock (this._updateLock)
        {
            this._settingsManager.Settings.SettingsChanged -= this.SettingsOnSettingsChanged;
            this._mediaService.CurrentMediaSourceChanged -= this.CurrentMediaSourceChanged;
            this._updateMediaInfo.Dispose();
        }
    }
}