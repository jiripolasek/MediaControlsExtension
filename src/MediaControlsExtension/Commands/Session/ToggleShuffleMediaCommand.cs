// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class ToggleShuffleSpecificMediaCommand : MediaSessionCommand
{
    public ToggleShuffleSpecificMediaCommand(IMediaService mediaService, MediaSession mediaSession, MediaCommandResultFactory resultFactory)
        : base(mediaService, mediaSession, MediaSessionOperations.ToggleShuffle, resultFactory)
    {
        this.Name = Strings.Command_ToggleShuffle!;
        this.Icon = Icons.ToggleShuffle;
    }
}