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

    [LoggerMessage(EventId = 22, Level = LogLevel.Trace, Message = "GSMTC delivered {PlaybackSignals} playback, {TimelineSignals} timeline, {MediaSignals} media, {SessionsSignals} sessions, and {CurrentSignals} current-session notification(s) before this snapshot.")]
    public static partial void NativeSignalsDrained(
        ILogger logger,
        long playbackSignals,
        long timelineSignals,
        long mediaSignals,
        long sessionsSignals,
        long currentSignals);

    [LoggerMessage(EventId = 23, Level = LogLevel.Trace, Message = "GSMTC native call #{CallId} {Operation} is starting for session {SessionId}/{BindingGeneration} ({ApplicationId}).")]
    public static partial void NativeCallStarting(
        ILogger logger,
        long callId,
        string operation,
        long sessionId,
        long bindingGeneration,
        string applicationId);

    [LoggerMessage(EventId = 24, Level = LogLevel.Trace, Message = "GSMTC native call #{CallId} {Operation} completed in {Elapsed} for session {SessionId}/{BindingGeneration} ({ApplicationId}).")]
    public static partial void NativeCallCompleted(
        ILogger logger,
        long callId,
        string operation,
        TimeSpan elapsed,
        long sessionId,
        long bindingGeneration,
        string applicationId);

    [LoggerMessage(EventId = 25, Level = LogLevel.Debug, Message = "GSMTC session reconciliation completed: {SessionCount} session(s), current {CurrentSessionId}, added {AddedCount}, retained {RetainedCount}, rebound {ReboundCount}, removed {RemovedCount}.")]
    public static partial void SessionReconciliationCompleted(
        ILogger logger,
        int sessionCount,
        long? currentSessionId,
        int addedCount,
        int retainedCount,
        int reboundCount,
        int removedCount);

    [LoggerMessage(EventId = 26, Level = LogLevel.Debug, Message = "GSMTC current-session reconciliation used {Path}; current session: {CurrentSessionId}, known sessions: {KnownSessionCount}.")]
    public static partial void CurrentSessionReconciled(
        ILogger logger,
        string path,
        long? currentSessionId,
        int knownSessionCount);

    [LoggerMessage(EventId = 27, Level = LogLevel.Trace, Message = "GSMTC manager call #{CallId} {Operation} is starting.")]
    public static partial void ManagerCallStarting(
        ILogger logger,
        long callId,
        string operation);

    [LoggerMessage(EventId = 28, Level = LogLevel.Trace, Message = "GSMTC manager call #{CallId} {Operation} completed in {Elapsed}.")]
    public static partial void ManagerCallCompleted(
        ILogger logger,
        long callId,
        string operation,
        TimeSpan elapsed);

    [LoggerMessage(EventId = 29, Level = LogLevel.Debug, Message = "Retaining missing GSMTC session {ApplicationId} for {GracePeriod} while waiting for recreation.")]
    public static partial void SessionRetentionStarted(
        ILogger logger,
        string applicationId,
        TimeSpan gracePeriod);

    [LoggerMessage(EventId = 30, Level = LogLevel.Information, Message = "Measured a transient GSMTC absence for application {ApplicationId}; future unambiguous removals receive up to {GracePeriod} of grace.")]
    public static partial void SessionRecreationGraceIncreased(
        ILogger logger,
        string applicationId,
        TimeSpan gracePeriod);

    [LoggerMessage(EventId = 31, Level = LogLevel.Debug, Message = "GSMTC session {ApplicationId} did not return within its retention grace.")]
    public static partial void SessionRetentionExpired(
        ILogger logger,
        string applicationId);

    [LoggerMessage(EventId = 32, Level = LogLevel.Error, Message = "The command-settle refresh callback failed.")]
    public static partial void CommandSettleRefreshFailed(
        ILogger logger,
        Exception exception);

    [LoggerMessage(EventId = 33, Level = LogLevel.Warning, Message = "Failed to retire GSMTC session {ApplicationId} after its native calls drained.")]
    public static partial void SessionRetirementFailed(
        ILogger logger,
        string applicationId,
        Exception exception);
}