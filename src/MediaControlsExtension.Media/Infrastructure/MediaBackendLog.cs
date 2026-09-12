// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using Microsoft.Extensions.Logging;

namespace JPSoftworks.MediaControlsExtension.Media.Infrastructure;

internal static partial class MediaBackendLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Media provider {ProviderId} failed to {Operation}.")]
    public static partial void ProviderFailed(ILogger logger, string providerId, string operation, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Could not pause a session from media provider {ProviderId}: {Status}.")]
    public static partial void PauseFailed(ILogger logger, string providerId, MediaBackendCommandStatus status);
}