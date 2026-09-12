// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using Microsoft.Extensions.Logging;

namespace JPSoftworks.MediaControlsExtension.Media.Diagnostics;

internal static partial class MediaLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Media service is starting.")]
    public static partial void ServiceStarting(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Media service is ready with {SessionCount} session(s).")]
    public static partial void ServiceReady(ILogger logger, int sessionCount);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Media service startup failed.")]
    public static partial void ServiceStartFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Media snapshot refresh failed.")]
    public static partial void SnapshotRefreshFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Media operation {OperationId} ({Operation}) was accepted for session {SessionId}.")]
    public static partial void CommandAccepted(
        ILogger logger,
        long operationId,
        MediaOperation operation,
        long sessionId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Media operation {OperationId} ({Operation}) failed for session {SessionId}: {Reason}")]
    public static partial void CommandFailed(
        ILogger logger,
        long operationId,
        MediaOperation operation,
        long sessionId,
        string reason);

    [LoggerMessage(EventId = 15, Level = LogLevel.Error, Message = "A media-state subscriber failed.")]
    public static partial void StateSubscriberFailed(
        ILogger logger,
        Exception exception);

    [LoggerMessage(EventId = 16, Level = LogLevel.Debug, Message = "Media command {Operation} was throttled for session {SessionId}.")]
    public static partial void CommandThrottled(
        ILogger logger,
        MediaOperation operation,
        long sessionId);

    [LoggerMessage(EventId = 17, Level = LogLevel.Debug, Message = "Media command {Operation} was rejected before admission: {Status}.")]
    public static partial void CommandRejected(
        ILogger logger,
        MediaOperation operation,
        MediaCommandSubmissionStatus status);

    [LoggerMessage(EventId = 18, Level = LogLevel.Debug, Message = "Media command {Operation} was rejected because a command admission limit was reached.")]
    public static partial void CommandAdmissionLimitReached(
        ILogger logger,
        MediaOperation operation);

    [LoggerMessage(EventId = 20, Level = LogLevel.Debug, Message = "Media refresh #{RefreshId} is starting in {Mode} mode after coalescing {RequestCount} request(s) ({Reasons}); burst credits: {BurstCredits}, topology credits: {TopologyBurstCredits}.")]
    public static partial void RefreshStarting(
        ILogger logger,
        long refreshId,
        MediaRefreshMode mode,
        int requestCount,
        MediaRefreshReason reasons,
        int burstCredits,
        int topologyBurstCredits);

    [LoggerMessage(EventId = 21, Level = LogLevel.Debug, Message = "Media refresh #{RefreshId} completed in {Elapsed}; sessions: {SessionCount}, current session: {CurrentSessionId}, status: {Status}.")]
    public static partial void RefreshCompleted(
        ILogger logger,
        long refreshId,
        TimeSpan elapsed,
        int sessionCount,
        long? currentSessionId,
        MediaServiceStatus status);

    [LoggerMessage(EventId = 32, Level = LogLevel.Error, Message = "The command-settle refresh callback failed.")]
    public static partial void CommandSettleRefreshFailed(
        ILogger logger,
        Exception exception);
}