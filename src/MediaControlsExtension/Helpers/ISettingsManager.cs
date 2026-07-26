// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal interface ISettingsManager
{
    bool ShowThumbnails { get; }
    bool ShowDetails { get; }
    GlobalCommandsMode GlobalCommands { get; }
    bool KeepOpen { get; }
    bool KeepOpenTogglePlayPauseCurrent { get; }
    bool KeepOpenSkipTrack { get; }
    bool KeepOpenTogglePlayMedia { get; }
    bool ShowToastMessages { get; }
    bool PauseOthersOnPlay { get; }
    bool ShowCurrentMediaAtTopLevel { get; }
    bool EnableVolumeControls { get; }
    bool ShowSkipCommands { get; }
    bool ShowSkipCommandsInDockBand { get; }
    string CommandPaletteIconThemeId { get; }
    string DockIconThemeId { get; }
}
