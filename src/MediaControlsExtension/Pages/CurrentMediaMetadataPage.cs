// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Pages;

#if FF_ENABLE_FULL_METADATA_PAGE
internal sealed partial class CurrentMediaMetadataPage : MediaMetadataPage
{
    public CurrentMediaMetadataPage(
        IMediaService mediaService,
        MediaSessionViewModelCache viewModels,
        MediaCommandResultFactory resultFactory,
        IIconService iconService)
        : base(
            mediaService,
            viewModels,
            sessionId: null,
            new CurrentSessionCommand(
                mediaService,
                MediaSessionOperations.SkipPreviousTrack,
                resultFactory)
            {
                Name = Strings.Command_PreviousTrack!,
            },
            new OptimisticPlaybackCommand(
                mediaService,
                resultFactory,
                iconService,
                IconSurface.CommandPalette),
            new CurrentSessionCommand(
                mediaService,
                MediaSessionOperations.SkipNextTrack,
                resultFactory)
            {
                Name = Strings.Command_NextTrack!,
            },
            new CurrentSessionCommand(
                mediaService,
                MediaSessionOperations.ToggleShuffle,
                resultFactory)
            {
                Name = Strings.Command_ToggleShuffle!,
            },
            new CurrentSessionCommand(
                mediaService,
                MediaSessionOperations.ToggleRepeat,
                resultFactory)
            {
                Name = Strings.Command_ToggleRepeat!,
            },
            new BringAssociatedAppToFrontCommand(
                mediaService,
                viewModels))
    {
        this.Id = "com.jpsoftworks.cmdpal.mediacontrols.currentmetadata";
    }

    protected override MediaSessionViewModel? ResolveViewModel()
        => this.MediaService.CurrentSession is { } session
            ? this.ViewModels.GetOrCreate(session)
            : null;

    protected override void SubscribeToTargetChanges()
        => this.MediaService.CurrentSessionChanged += this.MediaServiceOnCurrentSessionChanged;

    protected override void UnsubscribeFromTargetChanges()
        => this.MediaService.CurrentSessionChanged -= this.MediaServiceOnCurrentSessionChanged;

    private void MediaServiceOnCurrentSessionChanged(object? sender, EventArgs args)
        => this.RefreshTarget();
}
#endif
