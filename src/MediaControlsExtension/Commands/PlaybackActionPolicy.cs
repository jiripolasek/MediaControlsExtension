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

        var intent = session.PlaybackInfo.PrimaryOperation switch
        {
            MediaOperation.Play => PlaybackIntent.Play,
            MediaOperation.Stop => PlaybackIntent.Stop,
            _ => PlaybackIntent.Pause,
        };
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
}

internal readonly record struct PlaybackActionPresentation(
    PlaybackIntent Intent,
    string CommandName,
    IconInfo CommandIcon);