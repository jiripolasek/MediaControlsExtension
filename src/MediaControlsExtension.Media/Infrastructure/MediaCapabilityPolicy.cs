// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Media.Infrastructure;

internal static class MediaCapabilityPolicy
{
    /// <summary>Allows the opposite playback intent to queue while reported controls catch up.</summary>
    public static bool CanAdmitOperation(MediaOperation operation, MediaPlaybackInfoSnapshot playback) =>
        SupportsResolvedOperation(playback.Capabilities, operation, playback.EffectiveState) ||
        (playback.IsOptimistic &&
         ((playback.EffectiveState == MediaPlaybackState.Playing && operation == MediaOperation.Pause) ||
          (playback.EffectiveState == MediaPlaybackState.Paused && operation == MediaOperation.Play)));

    public static MediaOperation ResolvePrimaryOperation(
        MediaPlaybackState state, MediaCapabilities capabilities, bool isOptimistic = false)
    {
        if (state != MediaPlaybackState.Playing)
        {
            return MediaOperation.Play;
        }

        if (isOptimistic || SupportsResolvedOperation(capabilities, MediaOperation.Pause, state))
        {
            return MediaOperation.Pause;
        }

        return capabilities.HasFlag(MediaCapabilities.Stop)
            ? MediaOperation.Stop
            : MediaOperation.Pause;
    }

    /// <summary>Tests a resolved operation; resolve TogglePlayback before calling.</summary>
    public static bool SupportsResolvedOperation(
        MediaCapabilities capabilities,
        MediaOperation operation,
        MediaPlaybackState state)
    {
        var requiredCapability = operation switch
        {
            MediaOperation.Play => MediaCapabilities.Play,
            MediaOperation.Pause => MediaCapabilities.Pause,
            MediaOperation.Stop => MediaCapabilities.Stop,
            MediaOperation.SkipNext => MediaCapabilities.SkipNext,
            MediaOperation.SkipPrevious => MediaCapabilities.SkipPrevious,
            MediaOperation.ToggleShuffle => MediaCapabilities.ToggleShuffle,
            MediaOperation.ToggleRepeat => MediaCapabilities.ToggleRepeat,
            MediaOperation.ActivateSource => MediaCapabilities.ActivateSource,
            _ => MediaCapabilities.None
        };
        return requiredCapability != MediaCapabilities.None &&
               (capabilities.HasFlag(requiredCapability) ||
                (operation is MediaOperation.Play or MediaOperation.Pause &&
                 capabilities.HasFlag(MediaCapabilities.TogglePlayback) &&
                 state is MediaPlaybackState.Playing or MediaPlaybackState.Paused));
    }
}