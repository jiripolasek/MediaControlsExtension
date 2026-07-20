// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Text;
using Windows.Media;

namespace JPSoftworks.MediaControlsExtension.Pages;

internal sealed partial class MediaSourceListItem : ListItemBase, IDisposable
{
    private static readonly Tag PlayingTag = new() { Text = Strings.Tags_Playing!, Icon = Icons.PlaySolid, Foreground = new(true, new(0, 255, 0, 128)), Background = new(true, new(0, 255, 00, 40)) };

    private readonly SettingsManager _settingsManager;
    private readonly IIconService _iconService;
    private readonly IconSurface _iconSurface;
    private readonly ThrottledAction _throttledAction;
    private readonly OptimisticPlaybackCommand _command;
    private readonly BringAssociatedAppToFrontCommand _switchToApplicationCommand;
    private readonly ICommand _nextTrackCommand;
    private readonly ICommand _previousTrackCommand;
#if FF_ENABLE_FULL_METADATA_PAGE
    private readonly MediaMetadataPage _metadataPage;
#endif
    private readonly Lock _updateLock = new();

    private NiceIconInfo? _lastIcon;
    private MediaDetails? _mediaDetails;
    private MediaSource _mediaSource;
    private bool _disposed;
    private bool _asBand;

    public override IDetails? Details
    {
        get
        {
            lock (this._updateLock)
            {
                if (!this._disposed && this._mediaDetails is null)
                {
                    this._mediaSource.RequestHeroThumbnail();
                    ICommand? viewMetadataCommand = null;
#if FF_ENABLE_FULL_METADATA_PAGE
                    viewMetadataCommand = this._metadataPage;
#endif
                    this._mediaDetails = new(
                        this._previousTrackCommand,
                        this._command,
                        this._nextTrackCommand,
                        this._switchToApplicationCommand,
                        viewMetadataCommand);
                    this._mediaDetails.Update(this._mediaSource);
                }

                return this._mediaDetails?.Details;
            }
        }
        set
        {
        }
    }

    public MediaSourceListItem(
        MediaService mediaService,
        MediaSource mediaSource,
        SettingsManager settingsManager,
        YetAnotherHelper yetAnotherHelper,
        IIconService iconService,
        bool asBand) : base(new NoOpCommand())
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(mediaSource);
        ArgumentNullException.ThrowIfNull(settingsManager);
        ArgumentNullException.ThrowIfNull(yetAnotherHelper);
        ArgumentNullException.ThrowIfNull(iconService);

        this._mediaSource = mediaSource;
        this._settingsManager = settingsManager;
        this._iconService = iconService;
        this._iconSurface = asBand
            ? IconSurface.Dock
            : IconSurface.CommandPalette;
        this._throttledAction = new(100, () => this.Update(this._mediaSource));

        this._mediaSource.PropChanged += this.MediaSourceOnPropChanged;
        this._mediaSource.PlaybackPresentationChanged += this.MediaSourceOnPlaybackPresentationChanged;
        this._settingsManager.Settings.SettingsChanged += this.SettingsOnSettingsChanged;

        this.Title = Strings.Command_PlayPause!;
        this.Icon = iconService.GetIcon(ThemedIcon.PlayPause, this._iconSurface);

        this._asBand = asBand;

        this.Command = this._command = new(
            mediaService,
            settingsManager,
            yetAnotherHelper,
            iconService,
            this._iconSurface);
        this._switchToApplicationCommand = new(mediaSource);
        this._nextTrackCommand = new NextTrackInvokableSpecificMediaCommand(
            mediaService,
            mediaSource,
            yetAnotherHelper)
        {
            Icon = iconService.GetIcon(ThemedIcon.SkipNext, this._iconSurface),
        };
        this._previousTrackCommand = new PreviousTrackInvokableSpecificMediaCommand(
            mediaService,
            mediaSource,
            yetAnotherHelper)
        {
            Icon = iconService.GetIcon(ThemedIcon.SkipPrevious, this._iconSurface),
        };
        var toggleRepeatCommand = new ToggleRepeatSpecificMediaCommand(mediaService, mediaSource, yetAnotherHelper);
        var toggleShuffleCommand = new ToggleShuffleSpecificMediaCommand(mediaService, mediaSource, yetAnotherHelper);
#if FF_ENABLE_FULL_METADATA_PAGE
        this._metadataPage = MediaMetadataPage.ForSource(
            mediaService,
            mediaSource,
            this._previousTrackCommand,
            this._command,
            this._nextTrackCommand,
            toggleShuffleCommand,
            toggleRepeatCommand,
            this._switchToApplicationCommand);
#endif

        this.MoreCommands =
        [
            new CommandContextItem(this._switchToApplicationCommand) { RequestedShortcut = Chords.SwitchToApplication, Icon = Icons.SwitchApps },
#if FF_ENABLE_FULL_METADATA_PAGE
            new CommandContextItem(this._metadataPage) { Icon = Icons.Metadata },
#endif
            new CommandContextItem(this._nextTrackCommand) { RequestedShortcut = Chords.NextTrack, Icon = Icons.NextTrackOutline },
            new CommandContextItem(this._previousTrackCommand) { RequestedShortcut = Chords.PreviousTrack, Icon = Icons.PreviousTrackOutline },
            new CommandContextItem(toggleRepeatCommand) { RequestedShortcut = Chords.ToggleRepeat, Icon = Icons.ToggleRepeat },
            new CommandContextItem(toggleShuffleCommand) { RequestedShortcut = Chords.ToggleShuffle, Icon = Icons.ToggleShuffle },
        ];

        this.Update(this._mediaSource);
    }

    private void Update(MediaSource mediaSource)
    {
        ArgumentNullException.ThrowIfNull(mediaSource);

        lock (this._updateLock)
        {
            if (this._disposed)
            {
                return;
            }

            try
            {
                this.UpdateCore(mediaSource);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }
        }
    }

    private void UpdateCore(MediaSource mediaSource)
    {
        var isPlaying = mediaSource.DisplayedIsPlaying;

        this.Title = (isPlaying && !this._asBand ? "▶️ " : "") + mediaSource.Name;
        this.Subtitle = BuildSubtitle(mediaSource);
        this._command.UpdatePresentation(mediaSource);
        this.UpdateNavigationCommandIcons();
        this._mediaDetails?.Update(mediaSource);
        this.Tags = BuildTags();

        var iconBuildTask = BuildIcon(mediaSource, this._settingsManager.ShowThumbnails);
        if (this._lastIcon != iconBuildTask)
        {
            this._lastIcon = iconBuildTask;
            this.Icon = iconBuildTask.IconInfo;
        }

        return;

        static NiceIconInfo BuildIcon(MediaSource mediaSource, bool showThumbnail)
        {
            if (showThumbnail && mediaSource.ThumbnailInfo?.Stream != null)
            {
                return new(mediaSource.ThumbnailInfo!);
            }

            if (mediaSource.ApplicationIconPath != null)
            {
                return new(mediaSource.ApplicationIconPath);
            }

            return new(GetFallbackIconForPlaybackType(mediaSource.PlaybackType));
        }

        static string BuildSubtitle(MediaSource mediaSource)
        {
            var subtitleBuilder = new StringBuilder();
            subtitleBuilder.AppendWhenNotEmpty(" • ", mediaSource.Artist);
            subtitleBuilder.AppendWhenNotEmpty(" • ", mediaSource.ApplicationName);

#if DEBUG
            subtitleBuilder.AppendWhenNotEmpty(" • ", mediaSource.SourceAppUserModelId);
            subtitleBuilder.AppendWhenNotEmpty(" • ", Path.GetFileName(mediaSource.ApplicationIconPath));
#endif

            return subtitleBuilder.ToString();
        }

        ITag[] BuildTags()
        {
            var tags = new List<ITag>(2);
            if (mediaSource.DisplayedIsPlaying)
            {
                tags.Add(PlayingTag);
            }

            if (this._settingsManager.ShowThumbnails)
            {
                tags.Add(new Tag { Text = mediaSource.ApplicationName ?? "", Icon = new IconInfo(mediaSource.ApplicationIconPath) });
            }

            return [.. tags];
        }
    }

    private static IconInfo GetFallbackIconForPlaybackType(MediaPlaybackType playbackType)
    {
        return playbackType switch
        {
            MediaPlaybackType.Music => Icons.Music,
            MediaPlaybackType.Video => Icons.Video,
            MediaPlaybackType.Image => Icons.Image,
            _ => Icons.Unknown
        };
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

    private bool Equals(MediaSourceListItem other)
    {
        return ReferenceEquals(this._mediaSource, other._mediaSource);
    }

    public override bool Equals(object? obj)
    {
        return ReferenceEquals(this, obj) || obj is MediaSourceListItem other && this.Equals(other);
    }

    public override int GetHashCode()
    {
        return this._mediaSource.GetHashCode();
    }

    private void SettingsOnSettingsChanged(object sender, Settings args) => this.ScheduleUpdate();

    private void MediaSourceOnPropChanged(object sender, IPropChangedEventArgs args) => this.ScheduleUpdate();

    private void ScheduleUpdate()
    {
        lock (this._updateLock)
        {
            if (!this._disposed)
            {
                this._throttledAction.Invoke();
            }
        }
    }

    private void MediaSourceOnPlaybackPresentationChanged(object? sender, EventArgs args)
    {
        if (sender is MediaSource mediaSource)
        {
            this.Update(mediaSource);
        }
    }

    public void Dispose()
    {
        lock (this._updateLock)
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
        }

        try
        {
            this._throttledAction.Dispose();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }

        this._settingsManager.Settings.SettingsChanged -= this.SettingsOnSettingsChanged;
        this._mediaSource.PropChanged -= this.MediaSourceOnPropChanged;
        this._mediaSource.PlaybackPresentationChanged -= this.MediaSourceOnPlaybackPresentationChanged;
    }
}