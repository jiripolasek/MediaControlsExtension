// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed class SkipNextTrackMop : MediaSessionOp
{
    public override bool CanExecute(MediaSource source) => source.CanSkipNext;

    protected override async Task<MediaSessionOperationResult> InvokeUnderGateAsync(GlobalSystemMediaTransportControlsSessionManager manager, GlobalSystemMediaTransportControlsSession session)
    {
        bool success = session.GetPlaybackInfo().Controls.IsNextEnabled && await session.TrySkipNextAsync();
        return new(success ? $"⏭️ {Strings.Toast_SkippedNext}" : $"🚫 {Strings.Toast_CouldNotSkipNext}", success);
    }
}