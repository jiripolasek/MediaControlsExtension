// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;

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
    ActivateSource,
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
    string? DiagnosticMessage)
{
    /// <summary>Secondary pause outcomes; Status and DiagnosticMessage describe only the primary command.</summary>
    public ImmutableArray<MediaPauseOutcome> PauseOutcomes { get; init; } = [];
}

/// <summary>Reports a secondary pause without resolving its captured identity against current sessions.</summary>
public sealed record MediaPauseOutcome(
    MediaSessionId SessionId,
    long BindingGeneration,
    MediaCommandOutcomeStatus Status,
    string? DiagnosticMessage);

public sealed record MediaCommandSubmission(
    MediaCommandSubmissionStatus Status,
    MediaOperationId OperationId,
    long PublishedRevision,
    Task<MediaCommandOutcome>? Completion);