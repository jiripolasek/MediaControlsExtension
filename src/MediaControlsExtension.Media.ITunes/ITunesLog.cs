// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using Microsoft.Extensions.Logging;

namespace JPSoftworks.MediaControlsExtension.Media.ITunes;

internal static partial class ITunesLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to connect to iTunes COM interface.")]
    public static partial void ITunesConnectFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Could not hook iTunes connection point events.")]
    public static partial void ITunesHookEventsFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Error updating iTunes playback state.")]
    public static partial void ITunesUpdatePlaybackStateFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Failed to unadvise iTunes connection point events. HRESULT: 0x{HResult:X8}")]
    public static partial void ITunesUnhookEventsFailed(ILogger logger, int hResult);
}
