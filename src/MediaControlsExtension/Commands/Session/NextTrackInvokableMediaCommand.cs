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
        IIconService iconService,
        ILoggerFactory loggerFactory)
        : base(mediaService, initialization, MediaSessionOperations.SkipNextTrack, resultFactory, loggerFactory)
    {
        this.Name = Strings.Command_NextTrack!;
        this.Icon = iconService.GetIcon(ThemedIcon.SkipNext, IconSurface.CommandPalette);
    }
}

internal sealed partial class NextTrackInvokableSpecificMediaCommand : MediaSessionCommand
{
    public NextTrackInvokableSpecificMediaCommand(
        IMediaService mediaService,
        MediaSession mediaSession,
        MediaCommandResultFactory resultFactory,
        ILoggerFactory loggerFactory)
        : base(mediaService, mediaSession, MediaSessionOperations.SkipNextTrack, resultFactory, loggerFactory)
    {
        this.Name = Strings.Command_NextTrack!;
        this.Icon = Icons.SkipNextTrack;
    }
}
