// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class NextTrackInvokableMediaCommand : CurrentMediaSessionCommand
{
    public NextTrackInvokableMediaCommand(
        Task<GlobalSystemMediaTransportControlsSessionManager> manager,
        YetAnotherHelper yetAnotherHelper) : base(manager, MediaSessionOperations.SkipNextTrack, yetAnotherHelper)
    {
        this.Name = Strings.Command_NextTrack!;
        this.Icon = Icons.SkipNextTrack;
    }
}

internal sealed partial class NextTrackInvokableSpecificMediaCommand : MediaSessionCommand
{
    public NextTrackInvokableSpecificMediaCommand(MediaService mediaService, MediaSource mediaSource, YetAnotherHelper yetAnotherHelper)
        : base(mediaService, mediaSource, MediaSessionOperations.SkipNextTrack, yetAnotherHelper)
    {
        this.Name = Strings.Command_NextTrack!;
        this.Icon = Icons.SkipNextTrack;
    }
}