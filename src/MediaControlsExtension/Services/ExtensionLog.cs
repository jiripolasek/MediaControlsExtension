// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using Microsoft.Extensions.Logging;

namespace JPSoftworks.MediaControlsExtension.Services;

internal static partial class ExtensionLog
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Error,
        Message = "An unexpected extension error occurred.")]
    public static partial void UnexpectedError(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Error,
        Message = "{Message}")]
    public static partial void Error(ILogger logger, string message, Exception exception);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "{Message}")]
    public static partial void Warning(ILogger logger, string message);
}
