// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Services;

internal interface IIconService : IDisposable
{
    event EventHandler? IconsChanged;

    IReadOnlyList<IconThemeInfo> Themes { get; }

    IReadOnlyList<IconThemeDiagnostic> Diagnostics { get; }

    IconInfo GetIcon(
        ThemedIcon icon,
        IconSurface surface,
        IconState state = IconState.Default);
}

internal enum IconSurface
{
    CommandPalette,
    Dock,
}

internal enum ThemedIcon
{
    PlayPause,
    Play,
    Pause,
    SkipNext,
    SkipPrevious,
    NoMedia,
    ToggleMute,
    VolumeUp,
    VolumeDown,
    VolumeMute,
    VolumeZero,
    VolumeUnmute,
    VolumeLow,
    VolumeMedium,
    VolumeHigh,
}

internal enum IconState
{
    Default,
    Disabled,
}

internal sealed record IconThemeInfo(string Id, string DisplayName);

internal sealed record IconThemeDiagnostic(
    string ThemeId,
    string Message);
