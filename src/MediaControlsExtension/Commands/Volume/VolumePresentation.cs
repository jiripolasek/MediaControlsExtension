// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Globalization;
using System.Text;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal static class VolumePresentation
{
    private static readonly CompositeFormat LevelFormat = CompositeFormat.Parse(Strings.Volume_Level!);
    private static readonly CompositeFormat SetVolumeNameFormat = CompositeFormat.Parse(Strings.Command_SetVolume!);
    private static readonly CompositeFormat StatusFormat = CompositeFormat.Parse(Strings.Volume_Status!);

    public static string FormatLevel(int volumePercent)
        => string.Format(CultureInfo.CurrentCulture, LevelFormat, volumePercent);

    public static string FormatSetVolumeName(int volumePercent)
        => string.Format(CultureInfo.CurrentCulture, SetVolumeNameFormat, volumePercent);

    public static string FormatStatus(SystemVolumeState state)
        => state.IsMuted
            ? Strings.Volume_StatusMuted!
            : string.Format(CultureInfo.CurrentCulture, StatusFormat, state.VolumePercent);

    public static IconInfo GetIcon(SystemVolumeState state)
        => state.IsMuted ? Icons.Volume_Mute : GetIcon(state.VolumePercent);

    public static IconInfo GetIcon(int volumePercent)
    {
        return volumePercent switch
        {
            <= 0 => Icons.Volume_Unmute,
            <= 33 => Icons.Volume_Low,
            <= 66 => Icons.Volume_Mid,
            _ => Icons.Volume_Max,
        };
    }
}
