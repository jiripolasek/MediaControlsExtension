// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;

namespace JPSoftworks.MediaControlsExtension.Media;

/// <summary>Service operations, resolved to concrete provider commands when admitted.</summary>
public enum MediaOperation
{
    /// <summary>Starts playback and selects the target session on acceptance.</summary>
    Play,
    /// <summary>Pauses playback without changing the selected session.</summary>
    Pause,
    /// <summary>Stops playback without changing the selected session.</summary>
    Stop,
    /// <summary>Resolves to the target's current primary playback operation at admission.</summary>
    TogglePlayback,
    /// <summary>Advances to the next media item.</summary>
    SkipNext,
    /// <summary>Returns to the previous media item.</summary>
    SkipPrevious,
    /// <summary>Toggles the provider's shuffle setting.</summary>
    ToggleShuffle,
    /// <summary>Advances through the provider's repeat modes.</summary>
    ToggleRepeat,
    /// <summary>Plays the next available, play-capable session in published order, wrapping at the end.</summary>
    SwitchNextSession,
    /// <summary>Plays the previous available, play-capable session in published order, wrapping at the start.</summary>
    SwitchPreviousSession,
    /// <summary>Activates the owning source without selecting a session or predicting playback.</summary>
    ActivateSource,
}

/// <summary>Determines how command admission resolves the initial session target.</summary>
public enum MediaCommandTargetKind
{
    /// <summary>Use the session selected when the command is admitted.</summary>
    CurrentSession,
    /// <summary>Use the supplied service-local session ID.</summary>
    Session,
}

/// <summary>A session selector; admission captures the resolved ID and binding generation.</summary>
/// <param name="Kind">How to resolve the target.</param>
/// <param name="SessionId">Explicit target ID; ignored when Kind is CurrentSession.</param>
public readonly record struct MediaCommandTarget(
    MediaCommandTargetKind Kind,
    MediaSessionId SessionId)
{
    /// <summary>Gets a selector for the session current at admission, not at execution.</summary>
    public static MediaCommandTarget CurrentSession { get; } = new(
        MediaCommandTargetKind.CurrentSession,
        default);

    /// <summary>Creates an explicit session selector.</summary>
    /// <param name="sessionId">ID published by the receiving service.</param>
    /// <returns>A selector that remains tied to this logical session.</returns>
    public static MediaCommandTarget ForSession(MediaSessionId sessionId) => new(
        MediaCommandTargetKind.Session,
        sessionId);
}

/// <summary>A command request; creation alone neither validates nor executes it.</summary>
/// <param name="Target">Session selector, or navigation starting point for session-switch operations.</param>
/// <param name="Operation">Requested operation; capabilities are checked at admission.</param>
public readonly record struct MediaCommand(
    MediaCommandTarget Target,
    MediaOperation Operation);

/// <summary>Immediate admission result; only Accepted submissions have a completion task.</summary>
public enum MediaCommandSubmissionStatus
{
    /// <summary>The command was queued with captured targets; execution may still fail.</summary>
    Accepted,
    /// <summary>The service is stopped, starting, or disposing.</summary>
    NotReady,
    /// <summary>Controls are busy, the input was throttled, or command capacity was reached.</summary>
    Busy,
    /// <summary>The service is faulted or backend controls are unavailable.</summary>
    Unavailable,
    /// <summary>The operation is unsupported, or session navigation has no other playable target.</summary>
    Unsupported,
    /// <summary>The requested or current session is missing or unavailable.</summary>
    SessionGone,
}

/// <summary>Final status of an accepted command or reported secondary pause.</summary>
public enum MediaCommandOutcomeStatus
{
    /// <summary>The provider reported success; observed playback may not yet reflect it.</summary>
    Completed,
    /// <summary>The provider rejected the operation or execution failed.</summary>
    Failed,
    /// <summary>The provider stopped, timed out, or could not accept the operation.</summary>
    Unavailable,
    /// <summary>The target does not support the operation.</summary>
    Unsupported,
    /// <summary>The captured session binding disappeared, became unavailable, or was replaced.</summary>
    SessionGone,
    /// <summary>The service stopped before reporting completion; native work may still finish.</summary>
    Canceled,
}

/// <summary>Completion of an accepted command, retaining its original identity after session changes.</summary>
/// <param name="OperationId">ID assigned to the accepted submission.</param>
/// <param name="Status">Primary operation result.</param>
/// <param name="SessionId">Captured primary session ID, or null when no target is associated.</param>
/// <param name="DiagnosticMessage">Optional primary diagnostic text; not a machine-readable error code.</param>
public sealed record MediaCommandOutcome(
    MediaOperationId OperationId,
    MediaCommandOutcomeStatus Status,
    MediaSessionId? SessionId,
    string? DiagnosticMessage)
{
    /// <summary>Gets reported secondary pauses; these cannot turn primary success into failure or roll back its prediction.</summary>
    public ImmutableArray<MediaPauseOutcome> PauseOutcomes { get; init; } = [];
}

/// <summary>Reports a secondary pause without resolving its captured identity against current sessions.</summary>
/// <param name="SessionId">Originally captured service-local session ID.</param>
/// <param name="BindingGeneration">Originally captured binding version, even after removal or replacement.</param>
/// <param name="Status">Result of this pause only.</param>
/// <param name="DiagnosticMessage">Optional pause diagnostic text.</param>
public sealed record MediaPauseOutcome(
    MediaSessionId SessionId,
    long BindingGeneration,
    MediaCommandOutcomeStatus Status,
    string? DiagnosticMessage);

/// <summary>Immediate admission result and, when accepted, the eventual command outcome.</summary>
/// <param name="Status">Admission result; rejection has no execution side effects.</param>
/// <param name="OperationId">Accepted operation ID, or the default value when rejected.</param>
/// <param name="PublishedRevision">Service snapshot revision at admission; distinct from each session's Revision.</param>
/// <param name="Completion">Non-null only when accepted; completes with an outcome, including Canceled on service shutdown.</param>
public sealed record MediaCommandSubmission(
    MediaCommandSubmissionStatus Status,
    MediaOperationId OperationId,
    long PublishedRevision,
    Task<MediaCommandOutcome>? Completion);