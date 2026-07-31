// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class PlayPauseMediaCommand : StandaloneCurrentSessionCommand
{
    public PlayPauseMediaCommand(
        IMediaService mediaService,
        Task initialization,
        MediaCommandResultFactory resultFactory,
        IIconService iconService,
        ILoggerFactory loggerFactory)
        : base(mediaService, initialization, new PlayPauseMop(), resultFactory, loggerFactory)
    {
        // FallbackPlayCommandItem is using this command to update the name
        // so we can't override the Name property and we've to allow to set it to empty string
        this.Name = Strings.TogglePlayPause!;
        this.Icon = iconService.GetIcon(ThemedIcon.PlayPause, IconSurface.CommandPalette);
    }
}
