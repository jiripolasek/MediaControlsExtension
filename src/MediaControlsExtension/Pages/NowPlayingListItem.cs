// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Globalization;
using System.Text;

namespace JPSoftworks.MediaControlsExtension.Pages;

internal sealed partial class NowPlayingListItem : ListItemBase, IDisposable
{
    private static readonly CompositeFormat s_pauseFormat = CompositeFormat.Parse(Strings.NowPlaying_Pause!);
    private static readonly CompositeFormat s_stopFormat = CompositeFormat.Parse(Strings.NowPlaying_Stop!);
    private static readonly CompositeFormat s_playFormat = CompositeFormat.Parse(Strings.NowPlaying_Play!);
    private static readonly CompositeFormat s_nowPlayingFormat = CompositeFormat.Parse(Strings.NowPlaying_NowPlaying!);

    private readonly MediaService _mediaService;
    private readonly SettingsManager _settingsManager;
    private readonly ThrottledAction _updateMediaInfo;

    private readonly Lock _currentMediaSourceLock = new();
    private readonly Lock _updateLock = new();
    private readonly OptimisticPlaybackCommand _playPauseCommand;
    private readonly IContextItem[] _mediaContextCommands;
    private readonly bool _isBandPage;

    private MediaSource? _currentMediaSource;
    private bool _disposed;

    public NowPlayingListItem(MediaService mediaService, SettingsManager settingsManager, YetAnotherHelper yetAnotherHelper, bool asBandPage) : base(new NoOpCommand())
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(settingsManager);

        this._isBandPage = asBandPage;
        this._mediaService = mediaService;
        this._settingsManager = settingsManager;
        this._updateMediaInfo = new(150, this.UpdateCurrentMediaSource);

        this._mediaContextCommands = [
            new CommandContextItem(new BringAssociatedAppToFrontCommand(this._mediaService)) { RequestedShortcut = Chords.SwitchToApplication, Icon = Icons.SwitchApps },
            new CommandContextItem(new MediaCurrentSessionCommand(this._mediaService, MediaSessionOperations.SkipNextTrack, yetAnotherHelper) { Name = Strings.Command_NextTrack }) { RequestedShortcut = Chords.NextTrack, Icon = Icons.NextTrackOutline},
            new CommandContextItem(new MediaCurrentSessionCommand(this._mediaService, MediaSessionOperations.SkipPreviousTrack, yetAnotherHelper) { Name = Strings.Command_PreviousTrack }) { RequestedShortcut = Chords.PreviousTrack, Icon = Icons.PreviousTrackOutline},
            new CommandContextItem(new MediaCurrentSessionCommand(this._mediaService, MediaSessionOperations.ToggleRepeat, yetAnotherHelper) { Name = Strings.Command_ToggleRepeat }) { RequestedShortcut = Chords.ToggleRepeat, Icon = Icons.ToggleRepeat},
            new CommandContextItem(new MediaCurrentSessionCommand(this._mediaService, MediaSessionOperations.ToggleShuffle, yetAnotherHelper) { Name = Strings.Command_ToggleShuffle }) { RequestedShortcut = Chords.ToggleShuffle, Icon = Icons.ToggleShuffle},

            new CommandContextItem(new MediaCurrentSessionCommand(this._mediaService, new PlayNextSessionMop(this._settingsManager, this._mediaService), yetAnotherHelper) { Name = Strings.Command_NextApp })  { RequestedShortcut = Chords.NextSession, Icon = Icons.NextApp },
            new CommandContextItem(new MediaCurrentSessionCommand(this._mediaService, new PlayPreviousSessionMop(this._settingsManager, this._mediaService), yetAnotherHelper) { Name = Strings.Command_PreviousApp })  { RequestedShortcut = Chords.PreviousSession, Icon = Icons.PreviousApp },
        ];

        this.Command = this._playPauseCommand = new(this._mediaService, this._settingsManager, yetAnotherHelper)
        {
            Id = "com.jpsoftworks.cmdpal.mediacontrols.nowplaying",
            Icon = Icons.NoMedia
        };
        this.Title = this._isBandPage ? string.Empty : Strings.Command_PlayPause!;
        this.UpdateIcon(Icons.PlayPause);

        // Subscribe first, then seed by re-reading inside the lock: a handler that
        // ran in between installed a value the service already held, so the locked
        // re-read returns that value or a newer one and cannot resurrect a stale
        // source. The seed also wires the per-source events and schedules the
        // initial update.
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
            this._currentMediaSource.PlaybackPresentationChanged -= this.MediaSourceOnPlaybackPresentationChanged;
        }

        this._currentMediaSource = mediaSource;

        if (this._currentMediaSource != null)
        {
            this._currentMediaSource.PropChanged += this.MediaSourceOnPropChanged;
            this._currentMediaSource.PlaybackPresentationChanged += this.MediaSourceOnPlaybackPresentationChanged;
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
                this.Title = this._isBandPage ? string.Empty : Strings.NowPlaying_NothingPlaying!;
                this.Icon = Icons.NoMedia;
                this.Subtitle = this._isBandPage ? string.Empty : Strings.NowPlaying_Subtitle!;

                this._playPauseCommand.UpdatePresentation(null, showName: !this._isBandPage);

                this.MoreCommands = [];
            }
            else
            {
                var playbackAction = this._playPauseCommand.UpdatePresentation(
                    mediaSource,
                    showName: !this._isBandPage);

                this.Title = this._isBandPage
                    ? string.Empty
                    : playbackAction.Intent switch
                    {
                        PlaybackIntent.Play => string.Format(CultureInfo.CurrentCulture, s_playFormat, mediaSource.Name),
                        PlaybackIntent.Stop => string.Format(CultureInfo.CurrentCulture, s_stopFormat, mediaSource.Name),
                        _ => string.Format(CultureInfo.CurrentCulture, s_pauseFormat, mediaSource.Name)
                    };
                this.Subtitle = this._isBandPage
                    ? string.Empty
                    : StringHelper.JoinNonEmpty(" • ", string.Format(CultureInfo.CurrentCulture, s_nowPlayingFormat, mediaSource.Name), mediaSource.Artist, mediaSource.ApplicationName);

                this.UpdateIcon(playbackAction.CommandIcon);

                this.MoreCommands = this._mediaContextCommands;
            }
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
                this._updateMediaInfo.Invoke();
            }
        }
    }

    private void MediaSourceOnPlaybackPresentationChanged(object? sender, EventArgs args)
    {
        if (sender is MediaSource mediaSource)
        {
            lock (this._currentMediaSourceLock)
            {
                if (!this._disposed && ReferenceEquals(mediaSource, this._currentMediaSource))
                {
                    this.Update(mediaSource);
                }
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
            currentMediaSource.PlaybackPresentationChanged -= this.MediaSourceOnPlaybackPresentationChanged;
        }
    }
}