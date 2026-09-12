// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;

namespace JPSoftworks.MediaControlsExtension.Media;

/// <summary>Owns session monitoring, current-session selection, and asynchronous command execution.</summary>
/// <remarks>
/// Getters are safe to read concurrently; separate reads need not describe the same revision.
/// Events are coalesced on a background pump after publication; handlers read current state and dispatch to the UI as needed.
/// Dispose starts shutdown; DisposeAsync waits for shutdown and backend disposal. Neither permits restarting this instance.
/// </remarks>
public interface IMediaService : IDisposable, IAsyncDisposable
{
    /// <summary>Signals changed session membership or order; item changes use MediaSession.Changed.</summary>
    event EventHandler? SessionsChanged;

    /// <summary>Signals a different selected session, including selection becoming null.</summary>
    event EventHandler? CurrentSessionChanged;

    /// <summary>Signals a change to Status or Availability.</summary>
    event EventHandler? StatusChanged;

    /// <summary>Signals changed provider-state contents; handlers read Backends for the latest snapshot.</summary>
    event EventHandler? BackendsChanged;

    /// <summary>Gets provider states in registration order, including disabled providers; empty when unreported or stopped.</summary>
    ImmutableArray<MediaBackendState> Backends { get; }

    /// <summary>Gets an ordered immutable list of stable session objects; retained unavailable sessions may be included.</summary>
    ImmutableArray<MediaSession> Sessions { get; }

    /// <summary>Gets the selected available session, or null when none is available.</summary>
    /// <remarks>
    /// Accepted Play retains selection until its session becomes unavailable. Automatic selection prefers playing hints,
    /// other playing sessions, other hints, then remaining sessions. Ties retain the current session, then use published order.
    /// </remarks>
    MediaSession? CurrentSession { get; }

    /// <summary>Gets service lifecycle and aggregate observation health.</summary>
    MediaServiceStatus Status { get; }

    /// <summary>Gets aggregate control availability; individual targets still require availability and capability checks.</summary>
    MediaControlAvailability Availability { get; }

    /// <summary>Initializes the backend, publishes an initial snapshot, and starts monitoring.</summary>
    /// <param name="cancellationToken">Cancels the first caller's startup, or only a later caller's wait on that startup.</param>
    /// <returns>The shared startup result; repeated calls do not retry failed initialization.</returns>
    /// <exception cref="OperationCanceledException">Startup or this caller's wait was canceled.</exception>
    /// <exception cref="ObjectDisposedException">Shutdown has started.</exception>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Attempts immediate admission, capturing targets and publishing any playback prediction before execution.</summary>
    /// <param name="command">Requested operation and session selector.</param>
    /// <returns>Admission status and, only when accepted, an operation ID and completion task.</returns>
    /// <remarks>
    /// Commands sharing captured bindings are ordered; independent targets can progress concurrently.
    /// Acceptance is not execution success. Accepted Play selects its target; failure keeps that selection.
    /// </remarks>
    MediaCommandSubmission TrySubmit(MediaCommand command);

    /// <summary>Requests the exact published artwork version.</summary>
    /// <param name="key">Artwork key obtained from this service's session metadata.</param>
    /// <param name="cancellationToken">Cancels this request without canceling media commands.</param>
    /// <returns>Managed image content, or null when the key is obsolete or content is unavailable.</returns>
    ValueTask<MediaArtworkContent?> GetArtworkAsync(
        MediaArtworkKey key,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces options for subsequent admissions without changing already accepted commands.</summary>
    /// <param name="options">Non-null replacement options.</param>
    /// <exception cref="ArgumentNullException">Options are null.</exception>
    void UpdateOptions(MediaServiceOptions options);
}