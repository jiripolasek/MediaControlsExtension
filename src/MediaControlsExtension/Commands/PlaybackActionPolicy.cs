// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal static class PlaybackActionPolicy
{
    public static PlaybackActionPresentation GetPresentation(
        MediaSession? session,
        IIconService iconService,
        IconSurface surface)
    {
        ArgumentNullException.ThrowIfNull(iconService);

        if (session is null)
        {
            return new(
                PlaybackIntent.Toggle,
                Strings.Command_PlayPause!,
                iconService.GetIcon(ThemedIcon.PlayPause, surface));
        }

        var playback = session.PlaybackInfo;
        var intent = playback.IsOptimistic
            ? playback.EffectiveState == MediaPlaybackState.Playing
                ? PlaybackIntent.Pause
                : PlaybackIntent.Play
            : ResolveIntent(
                playback.ConfirmedState == MediaPlaybackState.Playing,
                playback.Capabilities.HasFlag(MediaCapabilities.Pause),
                playback.Capabilities.HasFlag(MediaCapabilities.Stop));
        return intent switch
        {
            PlaybackIntent.Play => new(
                intent,
                Strings.Command_Play!,
                iconService.GetIcon(ThemedIcon.Play, surface)),
            PlaybackIntent.Stop => new(
                intent,
                Strings.Command_Stop!,
                iconService.GetIcon(ThemedIcon.Pause, surface)),
            _ => new(
                intent,
                Strings.Command_Pause!,
                iconService.GetIcon(ThemedIcon.Pause, surface))
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