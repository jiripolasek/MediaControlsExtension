// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class NextTrackInvokableMediaCommand : StandaloneCurrentSessionCommand
{
    internal const string CommandId = "com.jpsoftworks.cmdpal.mediacontrols.next";

    public NextTrackInvokableMediaCommand(
        IMediaService mediaService,
        Task initialization,
        MediaCommandResultFactory resultFactory,
        ILoggerFactory loggerFactory)
        : base(mediaService, initialization, MediaSessionOperations.SkipNextTrack, resultFactory, loggerFactory)
    {
        this.Id = CommandId;
        this.Name = Strings.Command_NextTrack!;
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