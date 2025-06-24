// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Resources;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Media;
using Windows.System;

namespace JPSoftworks.MediaControlsExtension.Pages;

internal sealed partial class MediaSourceListItem : ListItem, IDisposable
{
    private readonly MediaSource _mediaSource;
    private readonly SettingsManager _settingsManager;
    private readonly ThrottledAction _throttledAction;

    public MediaSourceListItem(MediaSource mediaSource, SettingsManager settingsManager, ICommand command) : base(command)
    {
        ArgumentNullException.ThrowIfNull(mediaSource);
        ArgumentNullException.ThrowIfNull(settingsManager);

        this._mediaSource = mediaSource;
        this._settingsManager = settingsManager;

        this._mediaSource.PropChanged += this.MediaSourceOnPropChanged;
        this._settingsManager.Settings.SettingsChanged += this.SettingsOnSettingsChanged;

        this._throttledAction = new ThrottledAction(100, () => this.Update(this._mediaSource));

        this.Title = Strings.Command_PlayPause!;
        this.Icon = Icons.PlayPause;

        this.Update(this._mediaSource);

        // keyboard shortcuts follow Windows Media Player shortcuts
        this.MoreCommands =
        [
            new CommandContextItem(Strings.Command_SwitchToApplication!, name: Strings.Command_SwitchToApplication!, action: this.BringToFront) { RequestedShortcut = new KeyChord(VirtualKeyModifiers.Control, (int)VirtualKey.G, 0), Icon = Icons.SwitchApps},
            new CommandContextItem(Strings.Command_NextTrack!, action: this.NextTrack) { RequestedShortcut = new KeyChord(VirtualKeyModifiers.Control, (int)VirtualKey.F, 0), Icon = Icons.NextTrackOutline},
            new CommandContextItem(Strings.Command_PreviousTrack!, action: this.PreviousTrack) { RequestedShortcut = new KeyChord(VirtualKeyModifiers.Control, (int)VirtualKey.B, 0), Icon = Icons.PreviousTrackOutline},
            new CommandContextItem(Strings.Command_ToggleRepeat!, action: this.ToggleRepeat) { RequestedShortcut = new KeyChord(VirtualKeyModifiers.Control, (int)VirtualKey.T, 0), Icon = Icons.ToggleRepeat},
            new CommandContextItem(Strings.Command_ToggleShuffle!, action: this.ToggleShuffle) { RequestedShortcut = new KeyChord(VirtualKeyModifiers.Control, (int)VirtualKey.H, 0), Icon = Icons.ToggleShuffle},
        ];
    }

    private async void ToggleShuffle()
    {
        try
        {
            var isShuffleActive = this._mediaSource.Session.GetPlaybackInfo()?.IsShuffleActive == true;
            await this._mediaSource.Session.TryChangeShuffleActiveAsync(!isShuffleActive)!;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }
    }

    private async void ToggleRepeat()
    {
        try
        {
            var currentRepeatMode = this._mediaSource.Session.GetPlaybackInfo()?.AutoRepeatMode;
            if (currentRepeatMode == null)
                return;

            var nextRepeatModel = (currentRepeatMode.Value + 1 % 3);
            await this._mediaSource.Session.TryChangeAutoRepeatModeAsync(nextRepeatModel)!;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }
    }

    private async void NextTrack()
    {
        try
        {
            await this._mediaSource.Session.TrySkipNextAsync()!;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }
    }

    private async void PreviousTrack()
    {
        try
        {
            await this._mediaSource.Session.TrySkipPreviousAsync()!;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }
    }

    public void Dispose()
    {
        try
        {
            this._settingsManager.Settings.SettingsChanged -= this.SettingsOnSettingsChanged;
            this._mediaSource.PropChanged -= this.MediaSourceOnPropChanged;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }
    }

    private void BringToFront()
    {
        if (this._mediaSource.Session.SourceAppUserModelId == null)
            return;

        try
        {
            AppWindowHelper.TryBringToFront(this._mediaSource.Session.SourceAppUserModelId, this._mediaSource.Name);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }
    }


    private void SettingsOnSettingsChanged(object sender, Settings args)
    {
        this._throttledAction.Invoke();
    }

    private void MediaSourceOnPropChanged(object sender, IPropChangedEventArgs args)
    {
        this._throttledAction.Invoke();
    }


    private void Update(MediaSource mediaSource)
    {
        this.Title = (mediaSource.IsPlaying ? "▶️ " : "") + mediaSource.Name;
        this.Subtitle = BuildSubtitle(mediaSource);
        this.Icon = SetIconSource(mediaSource, this._settingsManager.ShowThumbnails);
        this.Tags = BuildTags();

        return;

        static IconInfo SetIconSource(MediaSource mediaSource, bool showThumbnail)
        {
            if (showThumbnail && mediaSource.Thumbnail != null)
            {
                return IconInfo.FromStream(mediaSource.Thumbnail);
            }

            if (mediaSource.ApplicationIconPath != null)
            {
                return new IconInfo(mediaSource.ApplicationIconPath);
            }

            return GetIconForPlaybackType(mediaSource.PlaybackType);
        }

        static string BuildSubtitle(MediaSource mediaSource)
        {
            var temp = new List<string>();

            if (!string.IsNullOrWhiteSpace(mediaSource.Artist))
            {
                temp.Add(mediaSource.Artist);
            }

            if (!string.IsNullOrWhiteSpace(mediaSource.ApplicationName))
            {
                temp.Add(mediaSource.ApplicationName);
            }

#if DEBUG
            temp.Add(mediaSource.Session.SourceAppUserModelId ?? "no AUMID");
            if (!string.IsNullOrWhiteSpace(mediaSource.ApplicationIconPath))
            {
                temp.Add(Path.GetFileName(mediaSource.ApplicationIconPath));
            }
#endif

            return string.Join(" • ", temp);
        }

        ITag[] BuildTags()
        {
            var tags = new List<ITag>();
            if (mediaSource.IsPlaying)
            {
                tags.Add(new Tag
                {
                    Text = Strings.Tags_Playing!,
                    Icon = Icons.PlaySolid,
                    Foreground = new OptionalColor(true, new Color(0, 255, 0, 128)),
                    Background = new OptionalColor(true, new Color(0, 255, 00, 40))
                });
            }
            if (this._settingsManager.ShowThumbnails)
            {
                tags.Add(new Tag { Text = mediaSource.ApplicationName ?? "", Icon = new IconInfo(mediaSource.ApplicationIconPath) });
            }
            return [.. tags];
        }
    }

    private static IconInfo GetIconForPlaybackType(MediaPlaybackType playbackType)
    {
        return playbackType switch
        {
            MediaPlaybackType.Music => Icons.Music,
            MediaPlaybackType.Video => Icons.Video,
            MediaPlaybackType.Image => Icons.Image,
            _ => Icons.Unknown
        };
    }
}