// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Microsoft.CommandPalette.Extensions.Toolkit;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal static class Icons
{
    public static readonly IconInfo PlayPause = IconHelpers.FromRelativePaths(
        @"Assets\Icons\PlayPause_LightTheme.svg",
        @"Assets\Icons\PlayPause_DarkTheme.svg");

    public static readonly IconInfo SkipNextTrack = IconHelpers.FromRelativePaths(
        @"Assets\Icons\SkipNext_LightTheme.svg",
        @"Assets\Icons\SkipNext_DarkTheme.svg");

    public static readonly IconInfo SkipPreviousTrack = IconHelpers.FromRelativePaths(
        @"Assets\Icons\SkipPrevious_LightTheme.svg",
        @"Assets\Icons\SkipPrevious_DarkTheme.svg");

    public static readonly IconInfo ToggleMute = IconHelpers.FromRelativePath(@"Assets\Icons\Muted_Color.svg");

    public static readonly IconInfo MainIcon = IconHelpers.FromRelativePaths(@"Assets\StoreLogo.light.png", @"Assets\StoreLogo.png");

    public static readonly IconInfo Music = new IconInfo("\uEC4F");
    public static readonly IconInfo Video = new IconInfo("\uE714");
    public static readonly IconInfo Image = new IconInfo("\uE8BA");
    public static readonly IconInfo Unknown = new IconInfo("\uE897");

    public static readonly IconInfo SwitchApps = new IconInfo("\uE8F9");

    public static readonly IconInfo ToggleRepeat = new IconInfo("\uE8EE");
    public static readonly IconInfo ToggleShuffle = new IconInfo("\uE8B1");
    public static readonly IconInfo NextTrackOutline = new IconInfo("\uE893");
    public static readonly IconInfo PreviousTrackOutline = new IconInfo("\uE892");
    public static readonly IconInfo Play = new IconInfo("\uE768");
    public static readonly IconInfo PlaySolid = new IconInfo("\uF5B0");
}