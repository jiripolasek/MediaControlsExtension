// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using MediaService = JPSoftworks.MediaControlsExtension.Services.MediaService;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class ToggleRepeatSpecificMediaCommand : MediaSessionCommand
{
    public ToggleRepeatSpecificMediaCommand(MediaService mediaService, MediaSource mediaSource, YetAnotherHelper yetAnotherHelper)
        : base(mediaService, mediaSource, MediaSessionOperations.ToggleRepeat, yetAnotherHelper)
    {
        this.Name = Strings.Command_ToggleRepeat!;
        this.Icon = Icons.ToggleRepeat;
    }
}