// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Media;
using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed class ToggleRepeatMop : MediaSessionOp
{
    public override async Task<MediaSessionOperationResult> InvokeAsync(GlobalSystemMediaTransportControlsSessionManager manager, GlobalSystemMediaTransportControlsSession session)
    {
        var canControlRepeat = session.GetPlaybackInfo().Controls.IsRepeatEnabled;
        if (!canControlRepeat)
        {
            return new("🚫 Repeat control is not available for this session", false);
        }

        var currentRepeatMode = session.GetPlaybackInfo().AutoRepeatMode;
        var nextRepeatMode = currentRepeatMode switch
        {
            MediaPlaybackAutoRepeatMode.None =>  MediaPlaybackAutoRepeatMode.Track,
            MediaPlaybackAutoRepeatMode.Track => MediaPlaybackAutoRepeatMode.List,
            MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.None,
            { } unknown => unknown,
            _ => throw new InvalidOperationException()
        };
        bool success = await session.TryChangeAutoRepeatModeAsync(nextRepeatMode);
        return new(success ? $"🔁 Repeat mode changed to {nextRepeatMode}" : "🚫 Could not change repeat mode", success);
    }
}