// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class PreviousTrackInvokableMediaCommand : StandaloneCurrentSessionCommand
{
    internal const string CommandId = "com.jpsoftworks.cmdpal.mediacontrols.previous";

    public PreviousTrackInvokableMediaCommand(
        IMediaService mediaService,
        Task initialization,
        MediaCommandResultFactory resultFactory,
        ILoggerFactory loggerFactory)
        : base(mediaService, initialization, MediaSessionOperations.SkipPreviousTrack, resultFactory, loggerFactory)
    {
        this.Id = CommandId;
        this.Name = Strings.Command_PreviousTrack!;
    }
}
internal sealed partial class PreviousTrackInvokableSpecificMediaCommand : MediaSessionCommand
{
    public PreviousTrackInvokableSpecificMediaCommand(
        IMediaService mediaService,
        MediaSession mediaSession,
        MediaCommandResultFactory resultFactory,
        ILoggerFactory loggerFactory)
        : base(mediaService, mediaSession, MediaSessionOperations.SkipPreviousTrack, resultFactory, loggerFactory)
    {
        this.Name = Strings.Command_PreviousTrack!;
        this.Icon = Icons.SkipPreviousTrack;
    }
}