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
            return new($"🚫 {Strings.Toast_ShuffleNotAvailable}", false);
        }

        var isShuffleActive = session.GetPlaybackInfo().IsShuffleActive ?? false;
        bool success = await session.TryChangeShuffleActiveAsync(!isShuffleActive);
        return new(success ? (isShuffleActive ? $"🔀 {Strings.Toast_ShuffleDisabled}" : $"🔀 {Strings.Toast_ShuffleEnabled}") : $"🚫 {Strings.Toast_CouldNotToggleShuffle}", success);
    }
}