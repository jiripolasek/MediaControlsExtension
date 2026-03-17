// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Globalization;
using System.Text;
using Windows.Media;
using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed class ToggleRepeatMop : MediaSessionOp
{
    private static readonly CompositeFormat s_repeatChangedFormat = CompositeFormat.Parse(Strings.Toast_RepeatChanged!);
    public override async Task<MediaSessionOperationResult> InvokeAsync(GlobalSystemMediaTransportControlsSessionManager manager, GlobalSystemMediaTransportControlsSession session)
    {
        var canControlRepeat = session.GetPlaybackInfo().Controls.IsRepeatEnabled;
        if (!canControlRepeat)
        {
            return new($"🚫 {Strings.Toast_RepeatNotAvailable}", false);
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
        return new(success ? $"🔁 {string.Format(CultureInfo.CurrentCulture, s_repeatChangedFormat, nextRepeatMode)}" : $"🚫 {Strings.Toast_CouldNotChangeRepeat}", success);
    }
}