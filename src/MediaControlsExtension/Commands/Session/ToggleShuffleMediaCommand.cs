// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using MediaService = JPSoftworks.MediaControlsExtension.Services.MediaService;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class ToggleShuffleSpecificMediaCommand : MediaSessionCommand
{
    public ToggleShuffleSpecificMediaCommand(MediaService mediaService, MediaSource mediaSource, YetAnotherHelper yetAnotherHelper)
        : base(mediaService, mediaSource, MediaSessionOperations.ToggleShuffle, yetAnotherHelper)
    {
        this.Name = Strings.Command_ToggleShuffle!;
        this.Icon = Icons.ToggleShuffle;
    }
}