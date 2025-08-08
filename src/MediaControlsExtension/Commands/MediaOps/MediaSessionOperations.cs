// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal static class MediaSessionOperations
{
    public static MediaSessionOp SkipPreviousTrack { get; } = new SkipPreviousTrackMop();
    public static MediaSessionOp SkipNextTrack { get; } = new SkipNextTrackMop();
    public static MediaSessionOp PlayPauseTrack { get; private set; } = new NoOpCommand();
    public static MediaSessionOp ToggleShuffle { get; } = new ToggleShuffleMop();
    public static MediaSessionOp ToggleRepeat { get; } = new ToggleRepeatMop();

    public static void Initialize(SettingsManager settingsManager)
    {
        PlayPauseTrack = new PlayPauseMop(settingsManager);
    }

    private sealed class NoOpCommand : MediaSessionOp
    {
        public override Task<MediaSessionOperationResult> InvokeAsync(GlobalSystemMediaTransportControlsSessionManager manager, GlobalSystemMediaTransportControlsSession session)
        {
            return Task.FromResult(new MediaSessionOperationResult("😢 Nothing happened", false));
        }
    }
}