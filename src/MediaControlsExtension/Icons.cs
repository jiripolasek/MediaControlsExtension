// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension;

internal static class Icons
{
    public static IconInfo MainIcon { get; } = IconHelpers.FromRelativePaths(
        @"Assets\MainIcon.light.png",
        @"Assets\MainIcon.dark.png");

    public static IconInfo PlayPause { get; } = IconHelpers.FromRelativePaths(
        @"Assets\IconThemes\colorful\PlayPause.light.svg",
        @"Assets\IconThemes\colorful\PlayPause.dark.svg");

    public static IconInfo SkipNextTrack { get; } = IconHelpers.FromRelativePaths(
        @"Assets\IconThemes\colorful\SkipNext.light.svg",
        @"Assets\IconThemes\colorful\SkipNext.dark.svg");

    public static IconInfo SkipPreviousTrack { get; } = IconHelpers.FromRelativePaths(
        @"Assets\IconThemes\colorful\SkipPrevious.light.svg",
        @"Assets\IconThemes\colorful\SkipPrevious.dark.svg");

    public static IconInfo ToggleMute { get; } = new(SegoeFluentIconGlyphs.ToggleMute);

    public static IconInfo Volume_Mute { get; } = new(SegoeFluentIconGlyphs.VolumeMute);
    public static IconInfo Volume_Up { get; } = new(SegoeFluentIconGlyphs.VolumeUp);
    public static IconInfo Volume_Down { get; } = new(SegoeFluentIconGlyphs.VolumeDown);
    public static IconInfo Volume_Low { get; } = new(SegoeFluentIconGlyphs.VolumeLow);
    public static IconInfo Volume_Max { get; } = new(SegoeFluentIconGlyphs.VolumeHigh);
    public static IconInfo Volume_Mid { get; } = new(SegoeFluentIconGlyphs.VolumeMedium);
    public static IconInfo Volume_Zero { get; } = new(SegoeFluentIconGlyphs.VolumeZero);

    public static IconInfo MediaHeroPlaceholder { get; } = IconHelpers.FromRelativePath(@"Assets\MediaHeroPlaceholder.png");

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

    public static IconInfo ReportProblem { get; } = new(SegoeFluentIconGlyphs.Bug);
    public static IconInfo DetailedLoggingEnabled { get; } = new(SegoeFluentIconGlyphs.CheckboxComposite);
    public static IconInfo DetailedLoggingDisabled { get; } = new(SegoeFluentIconGlyphs.Checkbox);
    public static IconInfo Save { get; } = new(SegoeFluentIconGlyphs.Save);
    public static IconInfo OpenInNewWindow { get; } = new(SegoeFluentIconGlyphs.OpenInNewWindow);
}

internal static class SegoeFluentIconGlyphs
{
    public const string VolumeMute = "\uE74F";
    public const string VolumeZero = "\uE992";
    public const string VolumeUnmute = "\uE767";
    public const string VolumeLow = "\uE993";
    public const string VolumeMedium = "\uE994";
    public const string VolumeHigh = "\uE995";

    public const string ToggleMute = VolumeMute;
    public const string VolumeUp = VolumeHigh;
    public const string VolumeDown = VolumeLow;

    public const string Bug = "\uEBE8";
    public const string Checkbox = "\uE739";
    public const string CheckboxComposite = "\uE73A";
    public const string Save = "\uE74E";
    public const string OpenInNewWindow = "\uE8A7";
}
