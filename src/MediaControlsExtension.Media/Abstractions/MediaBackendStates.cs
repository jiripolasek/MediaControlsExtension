// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;

namespace JPSoftworks.MediaControlsExtension.Media;

public enum MediaBackendLifecycleStatus
{
    Disabled,
    Starting,
    Ready,
    Stopping,
    Faulted,
}

public enum MediaConnectionStatus
{
    Unknown,
    Connecting,
    Connected,
    Disconnected,
}

/// <summary>Describes one provider-local connection, such as a browser profile.</summary>
public sealed record MediaConnectionState(
    string Id,
    string DisplayName,
    MediaConnectionStatus Status,
    string? DiagnosticMessage = null);

/// <summary>Reports connection state independently of session count and control availability.</summary>
public sealed record MediaBackendConnectionState(MediaConnectionStatus Status, string? DiagnosticMessage = null)
{
    public static MediaBackendConnectionState Unknown { get; } = new(MediaConnectionStatus.Unknown);
    public static MediaBackendConnectionState Connecting { get; } = new(MediaConnectionStatus.Connecting);
    public static MediaBackendConnectionState Connected { get; } = new(MediaConnectionStatus.Connected);
    public static MediaBackendConnectionState Disconnected { get; } = new(MediaConnectionStatus.Disconnected);

    public ImmutableArray<MediaConnectionState> Connections { get; init; } = [];

    internal bool HasSameContent(MediaBackendConnectionState other) =>
        this.Status == other.Status && this.DiagnosticMessage == other.DiagnosticMessage &&
        this.Connections.SequenceEqual(other.Connections);
}

public sealed record MediaBackendState(
    string Id,
    bool IsEnabled,
    MediaBackendLifecycleStatus Status,
    string? DiagnosticMessage)
{
    public string DisplayName { get; init; } = Id;

    public MediaBackendConnectionState Connection { get; init; } = MediaBackendConnectionState.Unknown;

    public int AvailableSessionCount { get; init; }

    internal bool HasSameContent(MediaBackendState other) =>
        this.Id == other.Id && this.IsEnabled == other.IsEnabled && this.Status == other.Status &&
        this.DiagnosticMessage == other.DiagnosticMessage && this.DisplayName == other.DisplayName &&
        this.AvailableSessionCount == other.AvailableSessionCount && this.Connection.HasSameContent(other.Connection);
}