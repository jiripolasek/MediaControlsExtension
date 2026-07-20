// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension;

internal static class Icons
{
    public static IconInfo PlayPause { get; } = IconHelpers.FromRelativePaths(
        @"Assets\IconThemes\colorful\PlayPause.light.svg",
        @"Assets\IconThemes\colorful\PlayPause.dark.svg");

    public static IconInfo PlayColorful { get; } = IconHelpers.FromRelativePaths(
        @"Assets\IconThemes\colorful\Play.light.svg",
        @"Assets\IconThemes\colorful\Play.dark.svg");

    public static IconInfo PauseColorful { get; } = IconHelpers.FromRelativePaths(
        @"Assets\IconThemes\colorful\Pause.light.svg",
        @"Assets\IconThemes\colorful\Pause.dark.svg");

    public static IconInfo SkipNextTrack { get; } = IconHelpers.FromRelativePaths(
        @"Assets\IconThemes\colorful\SkipNext.light.svg",
        @"Assets\IconThemes\colorful\SkipNext.dark.svg");

    public static IconInfo SkipPreviousTrack { get; } = IconHelpers.FromRelativePaths(
        @"Assets\IconThemes\colorful\SkipPrevious.light.svg",
        @"Assets\IconThemes\colorful\SkipPrevious.dark.svg");

    public static IconInfo SkipNextTrackDisabled { get; } = IconHelpers.FromRelativePaths(
        @"Assets\IconThemes\colorful\SkipNext.disabled.svg",
        @"Assets\IconThemes\colorful\SkipNext.disabled.svg");

    public static IconInfo SkipPreviousTrackDisabled { get; } = IconHelpers.FromRelativePaths(
        @"Assets\IconThemes\colorful\SkipPrevious.disabled.svg",
        @"Assets\IconThemes\colorful\SkipPrevious.disabled.svg");

    public static IconInfo NoMedia { get; } = IconHelpers.FromRelativePaths(
        @"Assets\IconThemes\colorful\NoMedia.svg",
        @"Assets\IconThemes\colorful\NoMedia.svg");

    public static IconInfo ToggleMute { get; } = IconHelpers.FromRelativePath(@"Assets\IconThemes\colorful\ToggleMute.svg");

    public static IconInfo Volume_Mute { get; } = IconHelpers.FromRelativePath(@"Assets\IconThemes\colorful\VolumeMute.svg");
    public static IconInfo Volume_Up { get; } = IconHelpers.FromRelativePath(@"Assets\IconThemes\colorful\VolumeUp.svg");
    public static IconInfo Volume_Down { get; } = IconHelpers.FromRelativePath(@"Assets\IconThemes\colorful\VolumeDown.svg");
    public static IconInfo Volume_Low { get; } = IconHelpers.FromRelativePath(@"Assets\IconThemes\colorful\VolumeLow.svg");
    public static IconInfo Volume_Max { get; } = IconHelpers.FromRelativePath(@"Assets\IconThemes\colorful\VolumeHigh.svg");
    public static IconInfo Volume_Mid { get; } = IconHelpers.FromRelativePath(@"Assets\IconThemes\colorful\VolumeMedium.svg");
    public static IconInfo Volume_Unmute { get; } = IconHelpers.FromRelativePath(@"Assets\IconThemes\colorful\VolumeOff.svg");

    public static IconInfo MainIcon { get; } = IconHelpers.FromRelativePaths(@"Assets\Square40x40-lightunplated.png", @"Assets\Square40x40-unplated.png");

    public static IconInfo Music { get; } = new IconInfo("\uEC4F");
    public static IconInfo Video { get; } = new IconInfo("\uE714");
    public static IconInfo Image { get; } = new IconInfo("\uE8BA");
    public static IconInfo Unknown { get; } = new IconInfo("\uE897");

    public static IconInfo SwitchApps { get; } = new IconInfo("\uE8F9");
    public static IconInfo Metadata { get; } = new IconInfo("\uE946");

    public static IconInfo ToggleRepeat { get; } = new IconInfo("\uE8EE");
    public static IconInfo ToggleShuffle { get; } = new IconInfo("\uE8B1");
    public static IconInfo NextTrackOutline { get; } = new IconInfo("\uE893");
    public static IconInfo PreviousTrackOutline { get; } = new IconInfo("\uE892");
    public static IconInfo Play { get; } = new IconInfo("\uE768");
    public static IconInfo PlaySolid { get; } = new IconInfo("\uF5B0");

    public static IconInfo NextApp { get; } = new IconInfo("\uE8B5");
    public static IconInfo PreviousApp { get; } = new IconInfo("\uEA52");
}
