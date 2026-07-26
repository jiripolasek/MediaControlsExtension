// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class ToggleRepeatSpecificMediaCommand : MediaSessionCommand
{
    public ToggleRepeatSpecificMediaCommand(IMediaService mediaService, MediaSession mediaSession, MediaCommandResultFactory resultFactory)
        : base(mediaService, mediaSession, MediaSessionOperations.ToggleRepeat, resultFactory)
    {
        this.Name = Strings.Command_ToggleRepeat!;
        this.Icon = Icons.ToggleRepeat;
    }
}