// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;

namespace JPSoftworks.MediaControlsExtension.Media.Infrastructure;

/// <summary>Identifies a logical session within one backend instance; never reuse it for a different session.</summary>
/// <param name="Value">Opaque instance-local value; not a persistent application identifier.</param>
public readonly record struct MediaBackendSessionId(long Value);

/// <summary>Identifies the session binding captured when a command is accepted.</summary>
/// <param name="SessionId">Session ID in the receiving backend's namespace.</param>
/// <param name="BindingGeneration">Captured binding version; a replacement must use a newer generation.</param>
public readonly record struct MediaBackendSessionTarget(MediaBackendSessionId SessionId, long BindingGeneration);

/// <summary>Coalescible invalidations that request a new complete backend snapshot.</summary>
[Flags]
public enum MediaBackendSignal
{
    /// <summary>No invalidation.</summary>
    None = 0,
    /// <summary>Session observations or control availability may have changed.</summary>
    ObservationsChanged = 1 << 0,
    /// <summary>Session membership, order, or bindings may have changed.</summary>
    SessionsChanged = 1 << 1,
    /// <summary>Automatic-selection hints may have changed.</summary>
    CurrentSessionChanged = 1 << 2,
    /// <summary>Provider lifecycle, connection details, or diagnostics may have changed, even without sessions.</summary>
    BackendsChanged = 1 << 3,
}

/// <summary>Result of one backend operation; cancellation may instead propagate as an exception.</summary>
public enum MediaBackendCommandStatus
{
    /// <summary>The provider reported success; observed playback may not yet reflect it.</summary>
    Completed,
    /// <summary>The provider rejected the operation or execution failed.</summary>
    Failed,
    /// <summary>Controls are temporarily unavailable, busy, stopped, or timed out.</summary>
    Unavailable,
    /// <summary>The target does not support the operation.</summary>
    Unsupported,
    /// <summary>The captured session binding is missing, unavailable, excluded, or replaced.</summary>
    SessionGone,
    /// <summary>A sent playback command could not be confirmed; do not replay dependent playback.</summary>
    Unconfirmed,
}

/// <summary>Cached observations to invalidate before a later snapshot read.</summary>
[Flags]
public enum MediaBackendObservationChanges
{
    /// <summary>No observation needs refreshing.</summary>
    None = 0,
    /// <summary>Refresh playback state and capabilities.</summary>
    Playback = 1 << 0,
    /// <summary>Refresh timeline properties.</summary>
    Timeline = 1 << 1,
}

/// <summary>Requests refreshed observations for the current binding of a local session.</summary>
/// <param name="SessionId">Session ID in the receiving backend's namespace; unknown IDs are ignored.</param>
/// <param name="Changes">Observation categories to invalidate.</param>
public readonly record struct MediaBackendObservationRequest(
    MediaBackendSessionId SessionId,
    MediaBackendObservationChanges Changes);

/// <summary>Immutable observed state of one logical session and its current binding.</summary>
/// <param name="Id">Instance-local session identity, preserved while the logical session remains present.</param>
/// <param name="BindingGeneration">Version incremented whenever the native or remote binding is replaced.</param>
/// <param name="MediaProperties">Non-null metadata; artwork keys use this backend's session namespace.</param>
/// <param name="TimelineProperties">Non-null observed timeline.</param>
/// <param name="PlaybackState">Confirmed provider state, without service predictions.</param>
/// <param name="Capabilities">Operations supported by this session.</param>
/// <param name="IsAvailable">Whether this binding is usable; false retains the session without admitting commands.</param>
public sealed record MediaBackendSessionSnapshot(
    MediaBackendSessionId Id,
    long BindingGeneration,
    MediaPropertiesSnapshot MediaProperties,
    MediaTimelinePropertiesSnapshot TimelineProperties,
    MediaPlaybackState PlaybackState,
    MediaCapabilities Capabilities,
    bool IsAvailable = true)
{
    /// <summary>Gets the connection and effective behavior; a replacement target requires a new binding generation.</summary>
    public MediaSessionOrigin Origin { get; init; } = MediaSessionOrigin.Local;
}

/// <summary>A complete, ordered backend view; published objects and collection contents must remain unchanged.</summary>
/// <param name="Revision">Nonnegative observation revision that never decreases or resets within this instance.</param>
/// <param name="Sessions">Initialized array of non-null sessions with distinct local IDs; order determines presentation.</param>
/// <param name="CurrentSessionHints">Initialized array of backend-local selection candidates; unavailable or remote-treated sessions are ignored.</param>
/// <param name="Availability">Backend control availability, independent of connection state and session count.</param>
public sealed record MediaBackendSnapshot(
    long Revision,
    ImmutableArray<MediaBackendSessionSnapshot> Sessions,
    ImmutableArray<MediaBackendSessionId> CurrentSessionHints,
    MediaControlAvailability Availability)
{
    /// <summary>Gets the applied source-policy revision; zero denotes the initial empty policy.</summary>
    public long SourcePolicyRevision { get; init; }

    /// <summary>Gets non-null connection state; report changes even when the session list is empty.</summary>
    public MediaBackendConnectionState Connection { get; init; } = MediaBackendConnectionState.Unknown;

    /// <summary>Gets registered provider states in registration order; leaf providers leave this array empty.</summary>
    public ImmutableArray<MediaBackendState> Backends { get; init; } = [];
}

/// <summary>A command resolved to captured bindings in the receiving backend's session namespace.</summary>
/// <param name="SessionId">Primary session to control.</param>
/// <param name="BindingGeneration">Primary binding version to validate again at execution.</param>
/// <param name="Operation">Concrete operation; the service resolves playback toggles and session navigation before dispatch.</param>
/// <param name="SessionsToPause">Initialized secondary pause targets for Play; the composite passes an empty array to leaves.</param>
public sealed record MediaBackendCommand(
    MediaBackendSessionId SessionId,
    long BindingGeneration,
    MediaOperation Operation,
    ImmutableArray<MediaBackendSessionTarget> SessionsToPause);

/// <summary>Primary command result with independent secondary pause results.</summary>
/// <param name="Status">Primary operation status, unaffected by secondary pause failures.</param>
/// <param name="DiagnosticMessage">Optional primary diagnostic text; not a machine-readable error code.</param>
public sealed record MediaBackendCommandResult(
    MediaBackendCommandStatus Status,
    string? DiagnosticMessage)
{
    /// <summary>Gets reported secondary pauses by captured binding; an empty array does not imply every pause succeeded.</summary>
    public ImmutableArray<MediaBackendPauseResult> PauseResults { get; init; } = [];
}

/// <summary>Reports a pause using the captured target in this backend's session namespace.</summary>
/// <param name="Target">Original pause target, even if its session has since disappeared or rebound.</param>
/// <param name="Status">Outcome of this secondary pause only.</param>
/// <param name="DiagnosticMessage">Optional pause diagnostic text.</param>
public sealed record MediaBackendPauseResult(
    MediaBackendSessionTarget Target,
    MediaBackendCommandStatus Status,
    string? DiagnosticMessage);

/// <summary>Owns one provider instance from startup through asynchronous disposal.</summary>
/// <remarks>
/// Observation, commands, artwork, and invalidations may overlap; implementations own native scheduling.
/// The owner cancels and drains its calls before disposal. Published snapshots must not retain native handles.
/// </remarks>
public interface IMediaBackend : IAsyncDisposable
{
    /// <summary>Initializes monitoring; success does not require a running application or session.</summary>
    /// <param name="cancellationToken">Cancels initialization; the owner disposes a failed or canceled instance.</param>
    /// <returns>Completion of initialization, without requiring a discovered session.</returns>
    /// <remarks>Call once per instance before observation or commands; source policies may be applied beforehand.</remarks>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Publishes invalidations and stays active across recoverable disconnections.</summary>
    /// <param name="cancellationToken">Cancels enumeration.</param>
    /// <returns>A single-consumer stream of coalesced flags; startup changes must survive until enumeration begins.</returns>
    /// <remarks>Normal completion before cancellation or disposal is a provider failure. Signals request reads, not state deltas.</remarks>
    IAsyncEnumerable<MediaBackendSignal> WatchAsync(CancellationToken cancellationToken);

    /// <summary>Returns a complete snapshot with revisions that do not reset on reconnection.</summary>
    /// <param name="cancellationToken">Cancels this observation.</param>
    /// <returns>A non-null snapshot with initialized arrays and immutable contents.</returns>
    /// <remarks>The composite serializes reads per provider; a failed read leaves cached sessions unavailable until recovery.</remarks>
    Task<MediaBackendSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken);

    /// <summary>Marks requested observations stale for a later read; unknown session IDs are ignored.</summary>
    /// <param name="requests">Initialized array of local sessions and observation categories.</param>
    void InvalidateObservations(
        ImmutableArray<MediaBackendObservationRequest> requests);

    /// <summary>Validates captured bindings at execution and never replays commands on replacement connections.</summary>
    /// <param name="command">Resolved command with captured local bindings.</param>
    /// <param name="cancellationToken">Requests cancellation; already executing native work may still finish.</param>
    /// <returns>The primary result and any reported secondary pauses; success does not confirm an observed state change.</returns>
    /// <remarks>Revalidate after internal waits and before side effects, including source-policy exclusions.</remarks>
    Task<MediaBackendCommandResult> ExecuteAsync(
        MediaBackendCommand command,
        CancellationToken cancellationToken);

    /// <summary>Returns content for the requested session and image version, or null for an obsolete key.</summary>
    /// <param name="key">Artwork key from this backend's snapshot, using its local session ID.</param>
    /// <param name="cancellationToken">Cancels this request.</param>
    /// <returns>Managed image bytes, or null for obsolete keys or unavailable content; never substitute another version.</returns>
    ValueTask<MediaArtworkContent?> GetArtworkAsync(
        MediaArtworkKey key,
        CancellationToken cancellationToken);
}