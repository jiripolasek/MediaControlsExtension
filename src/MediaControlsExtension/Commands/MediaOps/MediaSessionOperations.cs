// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal static class MediaSessionOperations
{
    public static MediaSessionOp SkipPreviousTrack { get; } = new SkipPreviousTrackMop();
    public static MediaSessionOp SkipNextTrack { get; } = new SkipNextTrackMop();
    public static MediaSessionOp ToggleShuffle { get; } = new ToggleShuffleMop();
    public static MediaSessionOp ToggleRepeat { get; } = new ToggleRepeatMop();
}