// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;

namespace JPSoftworks.MediaControlsExtension.Media.Infrastructure;

internal readonly record struct MediaBackendSessionId(long Value);

[Flags]
internal enum MediaBackendSignal
{
    None = 0,
    ObservationsChanged = 1 << 0,
    SessionsChanged = 1 << 1,
    CurrentSessionChanged = 1 << 2,
}

internal enum MediaBackendCommandStatus
{
    Completed,
    Failed,
    Unavailable,
    Unsupported,
    SessionGone,
}

[Flags]
internal enum MediaBackendObservationChanges
{
    None = 0,
    Playback = 1 << 0,
    Timeline = 1 << 1,
}

internal readonly record struct MediaBackendObservationRequest(
    MediaBackendSessionId SessionId,
    MediaBackendObservationChanges Changes);

internal sealed record MediaBackendSessionSnapshot(
    MediaBackendSessionId Id,
    long BindingGeneration,
    MediaPropertiesSnapshot MediaProperties,
    MediaTimelinePropertiesSnapshot TimelineProperties,
    MediaPlaybackState PlaybackState,
    MediaCapabilities Capabilities);

internal sealed record MediaBackendSnapshot(
    long Revision,
    ImmutableArray<MediaBackendSessionSnapshot> Sessions,
    MediaBackendSessionId? CurrentSessionId,
    MediaControlAvailability Availability);

internal sealed record MediaBackendCommand(
    MediaBackendSessionId SessionId,
    long BindingGeneration,
    MediaOperation Operation,
    ImmutableArray<MediaBackendSessionId> SessionsToPause);

internal sealed record MediaBackendCommandResult(
    MediaBackendCommandStatus Status,
    string? DiagnosticMessage);

internal interface IMediaBackend : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<MediaBackendSignal> WatchAsync(CancellationToken cancellationToken);

    Task<MediaBackendSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken);

    void InvalidateObservations(
        ImmutableArray<MediaBackendObservationRequest> requests);

    Task<MediaBackendCommandResult> ExecuteAsync(
        MediaBackendCommand command,
        CancellationToken cancellationToken);

    ValueTask<MediaArtworkContent?> GetArtworkAsync(
        MediaArtworkKey key,
        CancellationToken cancellationToken);
}
