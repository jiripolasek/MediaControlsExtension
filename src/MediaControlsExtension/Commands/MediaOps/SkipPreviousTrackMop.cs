// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed class SkipPreviousTrackMop : MediaSessionOp
{
    public override bool CanExecute(GlobalSystemMediaTransportControlsSessionManager manager, GlobalSystemMediaTransportControlsSession session)
    {
        return session.GetPlaybackInfo().Controls.IsPreviousEnabled;
    }

    public override async Task<MediaSessionOperationResult> InvokeAsync(GlobalSystemMediaTransportControlsSessionManager manager, GlobalSystemMediaTransportControlsSession session)
    {
        bool success = session.GetPlaybackInfo().Controls.IsPreviousEnabled && await session.TrySkipPreviousAsync();
        return new(success ? $"⏮️ {Strings.Toast_SkippedPrevious}" : $"🚫 {Strings.Toast_CouldNotSkipPrevious}", success);
    }
}