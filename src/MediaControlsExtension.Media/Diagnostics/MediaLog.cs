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

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "GSMTC {OperationKind} #{OperationId} {OperationName} has not completed within {Elapsed}.")]
    public static partial void NativeOperationSlow(
        ILogger logger,
        string operationKind,
        long operationId,
        string operationName,
        TimeSpan elapsed);

    [LoggerMessage(EventId = 8, Level = LogLevel.Error, Message = "GSMTC control circuit opened because operation #{OperationId} {OperationName} did not complete within {Elapsed}.")]
    public static partial void ControlCircuitOpened(
        ILogger logger,
        long operationId,
        string operationName,
        TimeSpan elapsed);

    [LoggerMessage(EventId = 9, Level = LogLevel.Error, Message = "GSMTC observations paused because observation #{OperationId} {OperationName} did not complete within {Elapsed}.")]
    public static partial void ObservationsPaused(
        ILogger logger,
        long operationId,
        string operationName,
        TimeSpan elapsed);

    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "GSMTC observations resumed after observation #{OperationId} {OperationName} returned.")]
    public static partial void ObservationsResumed(
        ILogger logger,
        long operationId,
        string operationName);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "GSMTC command {OperationName} was rejected because the control lane remained occupied for {Elapsed}.")]
    public static partial void CommandLaneBusy(
        ILogger logger,
        string operationName,
        TimeSpan elapsed);

    [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "GSMTC session {ApplicationId} became unavailable during {OperationName}.")]
    public static partial void StaleSession(
        ILogger logger,
        string applicationId,
        string operationName);

    [LoggerMessage(EventId = 13, Level = LogLevel.Warning, Message = "Could not pause GSMTC session {ApplicationId} before playing another session.")]
    public static partial void PauseOtherSessionFailed(
        ILogger logger,
        string applicationId,
        Exception exception);

    [LoggerMessage(EventId = 14, Level = LogLevel.Warning, Message = "Could not observe GSMTC session {ApplicationId}; retaining its last snapshot.")]
    public static partial void SessionObservationFailed(
        ILogger logger,
        string applicationId,
        Exception exception);

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

    [LoggerMessage(EventId = 18, Level = LogLevel.Debug, Message = "Media command {Operation} was rejected because the command mailbox is full.")]
    public static partial void CommandMailboxFull(
        ILogger logger,
        MediaOperation operation);

    [LoggerMessage(EventId = 19, Level = LogLevel.Warning, Message = "Could not observe GSMTC {ObservationPart} for session {ApplicationId}; retaining that part of its last snapshot.")]
    public static partial void SessionObservationPartFailed(
        ILogger logger,
        string applicationId,
        string observationPart,
        Exception exception);
}
