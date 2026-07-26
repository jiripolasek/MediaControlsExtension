// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Media;

public enum MediaOperation
{
    Play,
    Pause,
    Stop,
    TogglePlayback,
    SkipNext,
    SkipPrevious,
    ToggleShuffle,
    ToggleRepeat,
    SwitchNextSession,
    SwitchPreviousSession,
}

public enum MediaCommandTargetKind
{
    CurrentSession,
    Session,
}

public readonly record struct MediaCommandTarget(
    MediaCommandTargetKind Kind,
    MediaSessionId SessionId)
{
    public static MediaCommandTarget CurrentSession { get; } = new(
        MediaCommandTargetKind.CurrentSession,
        default);

    public static MediaCommandTarget ForSession(MediaSessionId sessionId) => new(
        MediaCommandTargetKind.Session,
        sessionId);
}

public readonly record struct MediaCommand(
    MediaCommandTarget Target,
    MediaOperation Operation);

public enum MediaCommandSubmissionStatus
{
    Accepted,
    NotReady,
    Busy,
    Unavailable,
    Unsupported,
    SessionGone,
}

public enum MediaCommandOutcomeStatus
{
    Completed,
    Failed,
    Unavailable,
    Unsupported,
    SessionGone,
    Canceled,
}

public sealed record MediaCommandOutcome(
    MediaOperationId OperationId,
    MediaCommandOutcomeStatus Status,
    MediaSessionId? SessionId,
    string? DiagnosticMessage);

public sealed record MediaCommandSubmission(
    MediaCommandSubmissionStatus Status,
    MediaOperationId OperationId,
    long PublishedRevision,
    Task<MediaCommandOutcome>? Completion);