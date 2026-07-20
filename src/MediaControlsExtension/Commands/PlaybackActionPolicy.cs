// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal static class PlaybackActionPolicy
{
    public static PlaybackActionPresentation GetPresentation(
        MediaSource? source,
        IIconService iconService,
        IconSurface surface)
    {
        ArgumentNullException.ThrowIfNull(iconService);

        if (source is null)
        {
            return new(
                PlaybackIntent.Toggle,
                Strings.Command_PlayPause!,
                iconService.GetIcon(ThemedIcon.PlayPause, surface));
        }

        // While an optimistic "playing" display awaits confirmation, the
        // capability flags still describe the paused state (players disable
        // pause while paused), so resolving from them would flash "Stop".
        // Assume Pause; the confirmed flags re-resolve the presentation.
        var displayedIsPlaying = source.DisplayedIsPlaying;
        var intent = displayedIsPlaying && !source.IsPlaying
            ? PlaybackIntent.Pause
            : ResolveIntent(displayedIsPlaying, source.CanPlay, source.CanPause, source.CanStop);
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

    public static PlaybackIntent ResolveIntent(bool isPlaying, bool canPlay, bool canPause, bool canStop)
    {
        if (!isPlaying)
        {
            return PlaybackIntent.Play;
        }

        if (canPause)
        {
            return PlaybackIntent.Pause;
        }

        // "Playing" with the Play control still enabled is a mid-transition
        // snapshot whose button flags have not caught up with the status yet
        // (players flip the status first, the controls a beat later). Pause is
        // about to become valid — presenting or issuing Stop here would act on
        // the stale paused-state flags. Genuine stop-only players report
        // playing with Play disabled, and still resolve to Stop below.
        if (canPlay)
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