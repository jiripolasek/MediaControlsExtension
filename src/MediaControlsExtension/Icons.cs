// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension;

internal static class Icons
{
    public static IconInfo PlayPause { get; } = IconHelpers.FromRelativePaths(
        @"Assets\Icons\PlayPause_LightTheme.svg",
        @"Assets\Icons\PlayPause_DarkTheme.svg");

    public static IconInfo PlayColorful { get; } = IconHelpers.FromRelativePaths(
        @"Assets\Icons\Play_LightTheme.svg",
        @"Assets\Icons\Play_DarkTheme.svg");

    public static IconInfo PauseColorful { get; } = IconHelpers.FromRelativePaths(
        @"Assets\Icons\Pause_LightTheme.svg",
        @"Assets\Icons\Pause_DarkTheme.svg");

    public static IconInfo SkipNextTrack { get; } = IconHelpers.FromRelativePaths(
        @"Assets\Icons\SkipNext_LightTheme.svg",
        @"Assets\Icons\SkipNext_DarkTheme.svg");

    public static IconInfo SkipPreviousTrack { get; } = IconHelpers.FromRelativePaths(
        @"Assets\Icons\SkipPrevious_LightTheme.svg",
        @"Assets\Icons\SkipPrevious_DarkTheme.svg");

    public static IconInfo SkipNextTrackDisabled { get; } = IconHelpers.FromRelativePaths(
        @"Assets\Icons\SkipNext_Disabled_DarkTheme.svg",
        @"Assets\Icons\SkipNext_Disabled_DarkTheme.svg");

    public static IconInfo SkipPreviousTrackDisabled { get; } = IconHelpers.FromRelativePaths(
        @"Assets\Icons\SkipPrevious_Disabled_DarkTheme.svg",
        @"Assets\Icons\SkipPrevious_Disabled_DarkTheme.svg");

    public static IconInfo NoMedia { get; } = IconHelpers.FromRelativePaths(
        @"Assets\Icons\NoMedia.svg",
        @"Assets\Icons\NoMedia.svg");

    public static IconInfo ToggleMute { get; } = IconHelpers.FromRelativePath(@"Assets\Icons\Muted_Color.svg");

    public static IconInfo Volume_Mute { get; } = IconHelpers.FromRelativePath(@"Assets\Icons\Volume_Mute.svg");
    public static IconInfo Volume_Low { get; } = IconHelpers.FromRelativePath(@"Assets\Icons\Volume_Low.svg");
    public static IconInfo Volume_Max { get; } = IconHelpers.FromRelativePath(@"Assets\Icons\Volume_Max.svg");
    public static IconInfo Volume_Mid { get; } = IconHelpers.FromRelativePath(@"Assets\Icons\Volume_Mid.svg");
    public static IconInfo Volume_Unmute { get; } = IconHelpers.FromRelativePath(@"Assets\Icons\Volume_Unmute.svg");

    public static IconInfo MainIcon { get; } = IconHelpers.FromRelativePaths(@"Assets\Square40x40-lightunplated.png", @"Assets\Square40x40-unplated.png");

    public static IconInfo Music { get; } = new IconInfo("\uEC4F");
    public static IconInfo Video { get; } = new IconInfo("\uE714");
    public static IconInfo Image { get; } = new IconInfo("\uE8BA");
    public static IconInfo Unknown { get; } = new IconInfo("\uE897");

    public static IconInfo SwitchApps { get; } = new IconInfo("\uE8F9");

    public static IconInfo ToggleRepeat { get; } = new IconInfo("\uE8EE");
    public static IconInfo ToggleShuffle { get; } = new IconInfo("\uE8B1");
    public static IconInfo NextTrackOutline { get; } = new IconInfo("\uE893");
    public static IconInfo PreviousTrackOutline { get; } = new IconInfo("\uE892");
    public static IconInfo Play { get; } = new IconInfo("\uE768");
    public static IconInfo PlaySolid { get; } = new IconInfo("\uF5B0");

    public static IconInfo NextApp { get; } = new IconInfo("\uE8B5");
    public static IconInfo PreviousApp { get; } = new IconInfo("\uEA52");
}