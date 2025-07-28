// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed class ToggleShuffleMop : MediaSessionOp
{
    public override async Task<MediaSessionOperationResult> InvokeAsync(GlobalSystemMediaTransportControlsSessionManager manager, GlobalSystemMediaTransportControlsSession session)
    {
        var canControlShuffle = session.GetPlaybackInfo().Controls.IsShuffleEnabled;
        if (!canControlShuffle)
        {
            return new("🚫 Shuffle control is not available for this session", false);
        }

        var isShuffleActive = session.GetPlaybackInfo().IsShuffleActive ?? false;
        bool success = await session.TryChangeShuffleActiveAsync(!isShuffleActive);
        return new(success ? (isShuffleActive ? "🔀 Shuffle disabled" : "🔀 Shuffle enabled") : "🚫 Could not toggle shuffle", success);
    }
}