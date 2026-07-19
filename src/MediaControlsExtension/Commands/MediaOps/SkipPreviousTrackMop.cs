// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed class SkipPreviousTrackMop : MediaSessionOp
{
    public override bool CanExecute(MediaSource source) => source.CanSkipPrevious;

    protected override async Task<MediaSessionOperationResult> InvokeUnderGateAsync(GlobalSystemMediaTransportControlsSessionManager manager, GlobalSystemMediaTransportControlsSession session)
    {
        bool success = session.GetPlaybackInfo().Controls.IsPreviousEnabled && await session.TrySkipPreviousAsync();
        return new(success ? $"⏮️ {Strings.Toast_SkippedPrevious}" : $"🚫 {Strings.Toast_CouldNotSkipPrevious}", success);
    }
}