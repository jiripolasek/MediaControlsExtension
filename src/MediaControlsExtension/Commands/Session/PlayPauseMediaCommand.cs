// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class PlayPauseMediaCommand : CurrentMediaSessionCommand
{
    public PlayPauseMediaCommand(Task<GlobalSystemMediaTransportControlsSessionManager> sessionManager, SettingsManager settingsManager, YetAnotherHelper yetAnotherHelper)
        : base(sessionManager, new PlayPauseMop(settingsManager), yetAnotherHelper)
    {
        // FallbackPlayCommandItem is using this command to update the name
        // so we can't override the Name property and we've to allow to set it to empty string
        this.Name = Strings.TogglePlayPause!;
        this.Icon = Icons.PlayPause;
    }
}