// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;

namespace JPSoftworks.MediaControlsExtension.Media.Infrastructure;

public readonly record struct MediaBackendSessionId(long Value);

/// <summary>Identifies the session binding captured when a command is accepted.</summary>
public readonly record struct MediaBackendSessionTarget(MediaBackendSessionId SessionId, long BindingGeneration);

[Flags]
public enum MediaBackendSignal
{
    None = 0,
    ObservationsChanged = 1 << 0,
    SessionsChanged = 1 << 1,
    CurrentSessionChanged = 1 << 2,
    BackendsChanged = 1 << 3,
}

public enum MediaBackendCommandStatus
{
    Completed,
    Failed,
    Unavailable,
    Unsupported,
    SessionGone,
}

[Flags]
public enum MediaBackendObservationChanges
{
    None = 0,
    Playback = 1 << 0,
    Timeline = 1 << 1,
}

public readonly record struct MediaBackendObservationRequest(
    MediaBackendSessionId SessionId,
    MediaBackendObservationChanges Changes);

public sealed record MediaBackendSessionSnapshot(
    MediaBackendSessionId Id,
    long BindingGeneration,
    MediaPropertiesSnapshot MediaProperties,
    MediaTimelinePropertiesSnapshot TimelineProperties,
    MediaPlaybackState PlaybackState,
    MediaCapabilities Capabilities,
    bool IsAvailable = true);

/// <param name="CurrentSessionHints">Provider candidates for automatic selection; the service owns the selected session.</param>
public sealed record MediaBackendSnapshot(
    long Revision,
    ImmutableArray<MediaBackendSessionSnapshot> Sessions,
    ImmutableArray<MediaBackendSessionId> CurrentSessionHints,
    MediaControlAvailability Availability)
{
    /// <summary>Identifies the source policy used to produce this snapshot; zero is the initial empty policy.</summary>
    public long SourcePolicyRevision { get; init; }

    /// <summary>Identifies the connection state of the backend.</summary>
    public MediaBackendConnectionState Connection { get; init; } = MediaBackendConnectionState.Unknown;

    /// <summary>Registered provider states published by a composite.</summary>
    public ImmutableArray<MediaBackendState> Backends { get; init; } = [];
}

public sealed record MediaBackendCommand(
    MediaBackendSessionId SessionId,
    long BindingGeneration,
    MediaOperation Operation,
    ImmutableArray<MediaBackendSessionTarget> SessionsToPause);

public sealed record MediaBackendCommandResult(
    MediaBackendCommandStatus Status,
    string? DiagnosticMessage)
{
    /// <summary>Secondary pause results, independent of the primary command status.</summary>
    public ImmutableArray<MediaBackendPauseResult> PauseResults { get; init; } = [];
}

/// <summary>Reports a pause using the captured target in this backend's session namespace.</summary>
public sealed record MediaBackendPauseResult(
    MediaBackendSessionTarget Target,
    MediaBackendCommandStatus Status,
    string? DiagnosticMessage);

/// <summary>Owns one provider instance from startup through asynchronous disposal.</summary>
public interface IMediaBackend : IAsyncDisposable
{
    /// <summary>Initializes monitoring; success does not require a running application or session.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Publishes invalidations and stays active across recoverable disconnections.</summary>
    IAsyncEnumerable<MediaBackendSignal> WatchAsync(CancellationToken cancellationToken);

    /// <summary>Returns a complete snapshot with revisions that do not reset on reconnection.</summary>
    Task<MediaBackendSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken);

    void InvalidateObservations(
        ImmutableArray<MediaBackendObservationRequest> requests);

    /// <summary>Validates captured bindings at execution and never replays commands on replacement connections.</summary>
    Task<MediaBackendCommandResult> ExecuteAsync(
        MediaBackendCommand command,
        CancellationToken cancellationToken);

    /// <summary>Returns content for the requested session and image version, or null for an obsolete key.</summary>
    ValueTask<MediaArtworkContent?> GetArtworkAsync(
        MediaArtworkKey key,
        CancellationToken cancellationToken);
}