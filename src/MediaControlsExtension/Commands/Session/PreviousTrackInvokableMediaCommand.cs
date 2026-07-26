// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class PreviousTrackInvokableMediaCommand : StandaloneCurrentSessionCommand
{
    public PreviousTrackInvokableMediaCommand(
        IMediaService mediaService,
        Task initialization,
        MediaCommandResultFactory resultFactory,
        IIconService iconService) : base(mediaService, initialization, MediaSessionOperations.SkipPreviousTrack, resultFactory)
    {
        this.Name = Strings.Command_PreviousTrack!;
        this.Icon = iconService.GetIcon(ThemedIcon.SkipPrevious, IconSurface.CommandPalette);
    }
}
internal sealed partial class PreviousTrackInvokableSpecificMediaCommand : MediaSessionCommand
{
    public PreviousTrackInvokableSpecificMediaCommand(IMediaService mediaService, MediaSession mediaSession, MediaCommandResultFactory resultFactory)
        : base(mediaService, mediaSession, MediaSessionOperations.SkipPreviousTrack, resultFactory)
    {
        this.Name = Strings.Command_PreviousTrack!;
        this.Icon = Icons.SkipPreviousTrack;
    }
}