// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal static class PlaybackActionPolicy
{
    public static PlaybackActionPresentation GetPresentation(MediaSource? source)
    {
        if (source is null)
        {
            return new(PlaybackIntent.Toggle, Strings.Command_PlayPause!, Icons.PlayPause);
        }

        var intent = ResolveIntent(source.DisplayedIsPlaying, source.CanPause, source.CanStop);
        return intent switch
        {
            PlaybackIntent.Play => new(intent, Strings.Command_Play!, Icons.PlayColorful),
            PlaybackIntent.Stop => new(intent, Strings.Command_Stop!, Icons.PauseColorful),
            _ => new(intent, Strings.Command_Pause!, Icons.PauseColorful)
        };
    }

    public static PlaybackIntent ResolveIntent(bool isPlaying, bool canPause, bool canStop)
    {
        if (!isPlaying)
        {
            return PlaybackIntent.Play;
        }

        if (canPause)
        {
            return PlaybackIntent.Pause;
        }

        return canStop ? PlaybackIntent.Stop : PlaybackIntent.Pause;
    }
}

internal readonly record struct PlaybackActionPresentation(
    PlaybackIntent Intent,
    string CommandName,
    IconInfo CommandIcon);