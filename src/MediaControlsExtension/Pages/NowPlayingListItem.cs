// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Pages;

internal sealed partial class NowPlayingListItem : ListItemBase, IDisposable
{
    private readonly MediaService _mediaService;
    private readonly SettingsManager _settingsManager;
    private readonly ThrottledAction _updateMediaInfo;

    private readonly Lock _currentMediaSourceLock = new();
    private readonly Lock _updateLock = new();
    private readonly MediaCurrentSessionCommand _playPauseCommand;
    private readonly IContextItem[] _mediaContextCommands;
    private readonly bool _isBandPage;

    private MediaSource? _currentMediaSource;

    public NowPlayingListItem(MediaService mediaService, SettingsManager settingsManager, YetAnotherHelper yetAnotherHelper, bool asBandPage) : base(new NoOpCommand())
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(settingsManager);

        this._isBandPage = asBandPage;
        this._mediaService = mediaService;
        this._mediaService.CurrentMediaSourceChanged += this.CurrentMediaSourceChanged;

        this._settingsManager = settingsManager;
        this._settingsManager.Settings.SettingsChanged += this.SettingsOnSettingsChanged;

        this._currentMediaSource = this._mediaService.CurrentSource;
        this._updateMediaInfo = new(150, () => this.Update(this._currentMediaSource));

        this._mediaContextCommands = [
            new CommandContextItem(new BringAssociatedAppToFrontCommand(this._mediaService)) { RequestedShortcut = Chords.SwitchToApplication, Icon = Icons.SwitchApps },
            new CommandContextItem(new MediaCurrentSessionCommand(this._mediaService, MediaSessionOperations.SkipNextTrack, yetAnotherHelper) { Name = Strings.Command_NextTrack }) { RequestedShortcut = Chords.NextTrack, Icon = Icons.NextTrackOutline},
            new CommandContextItem(new MediaCurrentSessionCommand(this._mediaService, MediaSessionOperations.SkipPreviousTrack, yetAnotherHelper) { Name = Strings.Command_PreviousTrack }) { RequestedShortcut = Chords.PreviousTrack, Icon = Icons.PreviousTrackOutline},
            new CommandContextItem(new MediaCurrentSessionCommand(this._mediaService, MediaSessionOperations.ToggleRepeat, yetAnotherHelper) { Name = Strings.Command_ToggleRepeat }) { RequestedShortcut = Chords.ToggleRepeat, Icon = Icons.ToggleRepeat},
            new CommandContextItem(new MediaCurrentSessionCommand(this._mediaService, MediaSessionOperations.ToggleShuffle, yetAnotherHelper) { Name = Strings.Command_ToggleShuffle }) { RequestedShortcut = Chords.ToggleShuffle, Icon = Icons.ToggleShuffle},

            new CommandContextItem(new MediaCurrentSessionCommand(this._mediaService, new PlayNextSessionMop(this._settingsManager, this._mediaService), yetAnotherHelper) { Name = Strings.Command_NextApp })  { RequestedShortcut = Chords.NextSession, Icon = Icons.NextApp },
            new CommandContextItem(new MediaCurrentSessionCommand(this._mediaService, new PlayPreviousSessionMop(this._settingsManager, this._mediaService), yetAnotherHelper) { Name = Strings.Command_PreviousApp })  { RequestedShortcut = Chords.PreviousSession, Icon = Icons.PreviousApp },
        ];

        this.Command = this._playPauseCommand = new(this._mediaService, MediaSessionOperations.PlayPauseTrack, yetAnotherHelper, id: "com.jpsoftworks.cmdpal.mediacontrols.nowplaying") { Icon = Icons.NoMedia };
        this.Title = _isBandPage ? string.Empty : Strings.Command_PlayPause!;
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
                this.Title = "Nothing is playing right now";
                this.Icon = Icons.NoMedia;
                this.Subtitle = "Now playing";

                this._playPauseCommand.Name = Strings.Command_PlayPause;
                this._playPauseCommand.UpdateIcon(Icons.PlayPause);

                this.MoreCommands = [];
            }
            else
            {
                IconInfo icon;
                string cmdName;

                if (mediaSource.IsPlaying)
                {
                    if (mediaSource.Session.GetPlaybackInfo().Controls.IsPauseEnabled)
                    {
                        this.Title = _isBandPage ? string.Empty : $"Pause {mediaSource.Name}";
                        cmdName = Strings.Command_Pause;
                    }
                    else if (mediaSource.Session.GetPlaybackInfo().Controls.IsStopEnabled)
                    {
                        this.Title = _isBandPage ? string.Empty : $"Stop {mediaSource.Name}";
                        cmdName = Strings.Command_Stop;
                    }
                    else
                    {
                        this.Title = _isBandPage ? string.Empty : $"Pause {mediaSource.Name}";
                        cmdName = Strings.Command_Pause;
                    }

                    icon = Icons.PauseColorful;
                    this.Subtitle = _isBandPage ? string.Empty : StringHelper.JoinNonEmpty(" • ", $"Now playing {mediaSource.Name}", mediaSource.Artist, mediaSource.ApplicationName);
                }
                else
                {
                    this.Title = _isBandPage ? string.Empty : $"Play {mediaSource.Name}";
                    this.Subtitle = _isBandPage ? string.Empty : StringHelper.JoinNonEmpty(" • ", $"Now playing {mediaSource.Name}", mediaSource.Artist, mediaSource.ApplicationName);
                    icon = Icons.PlayColorful;
                    cmdName = Strings.Command_Play;
                }

                this.UpdateIcon(icon);
                this._playPauseCommand.Name = _isBandPage ? string.Empty : cmdName;
                this._playPauseCommand.UpdateIcon(icon);

                this.MoreCommands = this._mediaContextCommands;
            }
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