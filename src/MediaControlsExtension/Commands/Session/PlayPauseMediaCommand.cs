// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Foundation;
using Windows.Media.Control;
using MediaService = JPSoftworks.MediaControlsExtension.Services.MediaService;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class PlayPauseMediaCommand : CurrentMediaSessionCommand
{
    public PlayPauseMediaCommand(IAsyncOperation<GlobalSystemMediaTransportControlsSessionManager> sessionManager, SettingsManager settingsManager, YetAnotherHelper yetAnotherHelper)
        : base(sessionManager, new PlayPauseMop(settingsManager), yetAnotherHelper)
    {
        // FallbackPlayCommandItem is using this command to update the name
        // so we can't override the Name property and we've to allow to set it to empty string
        this.Name = Strings.TogglePlayPause!;
        this.Icon = Icons.PlayPause;
    }
}

internal sealed partial class PlayPauseSpecificMediaCommand : MediaSessionCommand
{
    public override string Name
    {
        get;
        set
        {
            field = value;
            this.OnPropertyChanged(nameof(this.Name));
        }
    }

    public PlayPauseSpecificMediaCommand(MediaService mediaService, MediaSource mediaSource, SettingsManager settingsManager, YetAnotherHelper yetAnotherHelper)
        : base(mediaService, mediaSource, new PlayPauseMop(settingsManager), yetAnotherHelper)
    {
        this.Name = Strings.TogglePlayPause!;
        this.Icon = Icons.PlayPause;
    }
}