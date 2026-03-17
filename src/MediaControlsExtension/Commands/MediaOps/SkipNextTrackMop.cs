// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed class SkipNextTrackMop : MediaSessionOp
{
    public override bool CanExecute(GlobalSystemMediaTransportControlsSessionManager manager, GlobalSystemMediaTransportControlsSession session)
    {
        return session.GetPlaybackInfo().Controls.IsNextEnabled;
    }

    public override async Task<MediaSessionOperationResult> InvokeAsync(GlobalSystemMediaTransportControlsSessionManager manager, GlobalSystemMediaTransportControlsSession session)
    {
        bool success = session.GetPlaybackInfo().Controls.IsNextEnabled && await session.TrySkipNextAsync();
        return new(success ? $"⏭️ {Strings.Toast_SkippedNext}" : $"🚫 {Strings.Toast_CouldNotSkipNext}", success);
    }
}