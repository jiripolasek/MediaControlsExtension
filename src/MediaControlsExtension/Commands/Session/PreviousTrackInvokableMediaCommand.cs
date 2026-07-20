// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Media.Control;
using MediaService = JPSoftworks.MediaControlsExtension.Services.MediaService;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class PreviousTrackInvokableMediaCommand : CurrentMediaSessionCommand
{
    public PreviousTrackInvokableMediaCommand(
        Task<GlobalSystemMediaTransportControlsSessionManager> manager,
        YetAnotherHelper yetAnotherHelper,
        IIconService iconService) : base(manager, MediaSessionOperations.SkipPreviousTrack, yetAnotherHelper)
    {
        this.Name = Strings.Command_PreviousTrack!;
        this.Icon = iconService.GetIcon(ThemedIcon.SkipPrevious, IconSurface.CommandPalette);
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