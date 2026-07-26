// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class NextTrackInvokableMediaCommand : StandaloneCurrentSessionCommand
{
    public NextTrackInvokableMediaCommand(
        IMediaService mediaService,
        Task initialization,
        MediaCommandResultFactory resultFactory,
        IIconService iconService) : base(mediaService, initialization, MediaSessionOperations.SkipNextTrack, resultFactory)
    {
        this.Name = Strings.Command_NextTrack!;
        this.Icon = iconService.GetIcon(ThemedIcon.SkipNext, IconSurface.CommandPalette);
    }
}

internal sealed partial class NextTrackInvokableSpecificMediaCommand : MediaSessionCommand
{
    public NextTrackInvokableSpecificMediaCommand(IMediaService mediaService, MediaSession mediaSession, MediaCommandResultFactory resultFactory)
        : base(mediaService, mediaSession, MediaSessionOperations.SkipNextTrack, resultFactory)
    {
        this.Name = Strings.Command_NextTrack!;
        this.Icon = Icons.SkipNextTrack;
    }
}