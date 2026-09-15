// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;

namespace JPSoftworks.MediaControlsExtension.Media;

/// <summary>Runtime lifecycle of a registered provider, separate from its requested enablement and connection.</summary>
public enum MediaBackendLifecycleStatus
{
    /// <summary>No provider instance is active.</summary>
    Disabled,
    /// <summary>The instance is initializing or awaiting its first valid snapshot.</summary>
    Starting,
    /// <summary>The instance has published a valid snapshot and may remain ready while disconnected.</summary>
    Ready,
    /// <summary>The instance is retiring and draining outstanding work.</summary>
    Stopping,
    /// <summary>Startup, observation, monitoring, policy application, or disposal failed; inspect the diagnostic.</summary>
    Faulted,
}

/// <summary>Explicit transport state; an empty session list does not imply disconnection.</summary>
public enum MediaConnectionStatus
{
    /// <summary>Connection state is unreported or cannot currently be observed.</summary>
    Unknown,
    /// <summary>Initialization or a connection attempt is in progress.</summary>
    Connecting,
    /// <summary>A live connection exists, possibly with no sessions or unavailable controls.</summary>
    Connected,
    /// <summary>No live connection exists; an enabled provider may continue listening for reconnection.</summary>
    Disconnected,
}

/// <summary>Describes one provider-local connection, such as a browser profile.</summary>
/// <param name="Id">Nonblank ordinal ID, unique within the provider; not a command target or connection epoch.</param>
/// <param name="DisplayName">Nonblank localized connection label.</param>
/// <param name="Status">Explicit connection status.</param>
/// <param name="DiagnosticMessage">Optional current connection diagnostic; clear it when obsolete.</param>
public sealed record MediaConnectionState(
    string Id,
    string DisplayName,
    MediaConnectionStatus Status,
    string? DiagnosticMessage = null);

/// <summary>Reports connection state independently of session count and control availability.</summary>
/// <param name="Status">Provider-reported aggregate connection status.</param>
/// <param name="DiagnosticMessage">Optional aggregate diagnostic; clear it when obsolete.</param>
public sealed record MediaBackendConnectionState(MediaConnectionStatus Status, string? DiagnosticMessage = null)
{
    /// <summary>Gets an unknown state with no diagnostic or connection details.</summary>
    public static MediaBackendConnectionState Unknown { get; } = new(MediaConnectionStatus.Unknown);
    /// <summary>Gets a connecting state with no diagnostic or connection details.</summary>
    public static MediaBackendConnectionState Connecting { get; } = new(MediaConnectionStatus.Connecting);
    /// <summary>Gets a connected state with no diagnostic or connection details.</summary>
    public static MediaBackendConnectionState Connected { get; } = new(MediaConnectionStatus.Connected);
    /// <summary>Gets a disconnected state with no diagnostic or connection details.</summary>
    public static MediaBackendConnectionState Disconnected { get; } = new(MediaConnectionStatus.Disconnected);

    /// <summary>Gets ordered connection details with distinct IDs; use an initialized empty array when not reported.</summary>
    public ImmutableArray<MediaConnectionState> Connections { get; init; } = [];

    internal bool HasSameContent(MediaBackendConnectionState other) =>
        this.Status == other.Status && this.DiagnosticMessage == other.DiagnosticMessage &&
        this.Connections.SequenceEqual(other.Connections);
}

/// <summary>Published state of one registration, including disabled providers.</summary>
/// <param name="Id">Stable, case-sensitive registration ID.</param>
/// <param name="IsEnabled">Requested enablement; remains true during startup, disconnection, and faults until disabled.</param>
/// <param name="Status">Current runtime lifecycle status.</param>
/// <param name="DiagnosticMessage">Optional lifecycle diagnostic, separate from connection diagnostics.</param>
public sealed record MediaBackendState(
    string Id,
    bool IsEnabled,
    MediaBackendLifecycleStatus Status,
    string? DiagnosticMessage)
{
    /// <summary>Gets the localized registration name; defaults to the registration ID.</summary>
    public string DisplayName { get; init; } = Id;

    /// <summary>Gets the optional ordinal group shared by mutually exclusive registrations.</summary>
    public string? ExclusiveGroup { get; init; }

    /// <summary>Gets explicit connection state, independent of lifecycle and command availability.</summary>
    public MediaBackendConnectionState Connection { get; init; } = MediaBackendConnectionState.Unknown;

    /// <summary>Gets the count of usable published sessions, excluding unavailable, retired, or source-excluded bindings.</summary>
    public int AvailableSessionCount { get; init; }

    internal bool HasSameContent(MediaBackendState other) =>
        this.Id == other.Id && this.ExclusiveGroup == other.ExclusiveGroup && this.IsEnabled == other.IsEnabled && this.Status == other.Status &&
        this.DiagnosticMessage == other.DiagnosticMessage && this.DisplayName == other.DisplayName &&
        this.AvailableSessionCount == other.AvailableSessionCount && this.Connection.HasSameContent(other.Connection);
}