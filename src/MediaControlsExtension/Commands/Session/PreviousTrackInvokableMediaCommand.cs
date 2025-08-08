// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Foundation;
using Windows.Media.Control;
using MediaService = JPSoftworks.MediaControlsExtension.Services.MediaService;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class PreviousTrackInvokableMediaCommand : CurrentMediaSessionCommand
{
    public PreviousTrackInvokableMediaCommand(
        IAsyncOperation<GlobalSystemMediaTransportControlsSessionManager> manager,
        YetAnotherHelper yetAnotherHelper) : base(manager, MediaSessionOperations.SkipPreviousTrack, yetAnotherHelper)
    {
        this.Name = Strings.Command_PreviousTrack!;
        this.Icon = Icons.SkipPreviousTrack;
    }
}
internal sealed partial class PreviousTrackInvokableSpecificMediaCommand : MediaSessionCommand
{
    public PreviousTrackInvokableSpecificMediaCommand(MediaService mediaService, MediaSource mediaSource, YetAnotherHelper yetAnotherHelper)
        : base(mediaService, mediaSource, MediaSessionOperations.SkipPreviousTrack, yetAnotherHelper)
    {
        this.Name = Strings.Command_PreviousTrack!;
        this.Icon = Icons.SkipPreviousTrack;
    }
}