// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;

namespace JPSoftworks.MediaControlsExtension.Media;

/// <summary>Service lifecycle and aggregate observation health.</summary>
public enum MediaServiceStatus
{
    /// <summary>The service has not started or has begun shutdown.</summary>
    Stopped,
    /// <summary>Backend startup and the initial snapshot are pending.</summary>
    Starting,
    /// <summary>Monitoring is running and aggregate controls are available; sessions may be empty.</summary>
    Ready,
    /// <summary>Monitoring started, but observation failed or aggregate controls are not available.</summary>
    Degraded,
    /// <summary>Startup failed; the instance cannot be restarted.</summary>
    Faulted,
}

/// <summary>Aggregate control admission state, separate from connectivity and per-session capabilities.</summary>
public enum MediaControlAvailability
{
    /// <summary>Commands cannot currently be admitted.</summary>
    Unavailable,
    /// <summary>Commands may be admitted after target and capability checks.</summary>
    Available,
    /// <summary>Control capacity is temporarily occupied.</summary>
    Busy,
    /// <summary>A protective circuit blocks controls after a backend failure or timeout.</summary>
    CircuitOpen,
}

/// <summary>Provider-observed playback state, or a service prediction of that state.</summary>
public enum MediaPlaybackState
{
    /// <summary>No playback state is known.</summary>
    Unknown,
    /// <summary>The player is closed.</summary>
    Closed,
    /// <summary>The player is open without active playback.</summary>
    Opened,
    /// <summary>The player is changing media or playback state.</summary>
    Changing,
    /// <summary>Playback is stopped.</summary>
    Stopped,
    /// <summary>Playback is running.</summary>
    Playing,
    /// <summary>Playback is paused.</summary>
    Paused,
}

/// <summary>Provider-reported media category.</summary>
public enum MediaContentType
{
    /// <summary>No supported category was reported.</summary>
    Unknown,
    /// <summary>Music or audio content.</summary>
    Music,
    /// <summary>Video content.</summary>
    Video,
    /// <summary>Still-image content.</summary>
    Image,
}

/// <summary>Operations supported by a session; availability is checked separately.</summary>
[Flags]
public enum MediaCapabilities
{
    /// <summary>No operations are supported.</summary>
    None = 0,
    /// <summary>Playback can be started.</summary>
    Play = 1 << 0,
    /// <summary>Playback can be paused.</summary>
    Pause = 1 << 1,
    /// <summary>Playback can be stopped.</summary>
    Stop = 1 << 2,
    /// <summary>The next media item can be selected.</summary>
    SkipNext = 1 << 3,
    /// <summary>The previous media item can be selected.</summary>
    SkipPrevious = 1 << 4,
    /// <summary>Shuffle can be toggled.</summary>
    ToggleShuffle = 1 << 5,
    /// <summary>Repeat mode can be changed.</summary>
    ToggleRepeat = 1 << 6,
    /// <summary>The owning source can be activated, independently of playback or native application metadata.</summary>
    ActivateSource = 1 << 7,
}

/// <summary>Immutable metadata and source presentation for one session.</summary>
/// <param name="Source">Non-null source presentation; it does not identify the command target.</param>
/// <param name="Title">Media title, or empty when unknown.</param>
/// <param name="Artist">Artist name, or empty when unknown.</param>
/// <param name="AlbumTitle">Album title, or empty when unknown.</param>
/// <param name="AlbumArtist">Album artist, or empty when unknown.</param>
/// <param name="Subtitle">Additional media subtitle, or empty when unknown.</param>
/// <param name="Genres">Initialized genre list; empty when unknown.</param>
/// <param name="TrackNumber">Provider-reported track number, or zero when unknown.</param>
/// <param name="AlbumTrackCount">Provider-reported album track count, or zero when unknown.</param>
/// <param name="ContentType">Provider-reported content category.</param>
/// <param name="Artwork">Versioned artwork key, or null when no image is available.</param>
public sealed record MediaPropertiesSnapshot(
    MediaSourceSnapshot Source,
    string Title,
    string Artist,
    string AlbumTitle,
    string AlbumArtist,
    string Subtitle,
    ImmutableArray<string> Genres,
    int TrackNumber,
    int AlbumTrackCount,
    MediaContentType ContentType,
    MediaArtworkKey? Artwork)
{
    /// <summary>Creates metadata with empty text, zero track counts, unknown content type, and no artwork.</summary>
    /// <param name="source">Source presentation to retain.</param>
    /// <returns>An empty metadata snapshot for this source.</returns>
    public static MediaPropertiesSnapshot Empty(MediaSourceSnapshot source) => new(
        source,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        [],
        0,
        0,
        MediaContentType.Unknown,
        null);
}

/// <summary>An observed timeline; Position is a sample, not a continuously advancing clock.</summary>
/// <param name="StartTime">Start offset on the provider's timeline.</param>
/// <param name="EndTime">End offset on the same timeline.</param>
/// <param name="MinSeekTime">Provider-reported lower seek bound; does not imply a supported seek command.</param>
/// <param name="MaxSeekTime">Provider-reported upper seek bound.</param>
/// <param name="Position">Playback position at the reported update time.</param>
/// <param name="LastUpdatedAt">Provider-reported sample timestamp, or null when unknown.</param>
public sealed record MediaTimelinePropertiesSnapshot(
    TimeSpan StartTime,
    TimeSpan EndTime,
    TimeSpan MinSeekTime,
    TimeSpan MaxSeekTime,
    TimeSpan Position,
    DateTimeOffset? LastUpdatedAt)
{
    /// <summary>Gets zero offsets with no update timestamp or known duration.</summary>
    public static MediaTimelinePropertiesSnapshot Empty { get; } = new(
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        null);

    /// <summary>Gets EndTime minus StartTime when positive; otherwise null.</summary>
    public TimeSpan? Duration => this.EndTime > this.StartTime
        ? this.EndTime - this.StartTime
        : null;
}

/// <summary>Confirmed playback with the service's optional temporary prediction.</summary>
/// <param name="ConfirmedState">Latest provider-observed playback state.</param>
/// <param name="EffectiveState">Predicted state when optimistic; otherwise the confirmed state.</param>
/// <param name="IsOptimistic">Whether an accepted command is temporarily overriding observed playback.</param>
/// <param name="Capabilities">Latest supported operations, independent of the prediction.</param>
/// <param name="PrimaryOperation">Resolved playback-toggle action; capability checks may still reject it.</param>
public sealed record MediaPlaybackInfoSnapshot(
    MediaPlaybackState ConfirmedState,
    MediaPlaybackState EffectiveState,
    bool IsOptimistic,
    MediaCapabilities Capabilities,
    MediaOperation PrimaryOperation);

internal sealed record MediaSessionState(
    long Revision,
    bool IsAvailable,
    MediaPropertiesSnapshot MediaProperties,
    MediaTimelinePropertiesSnapshot TimelineProperties,
    MediaPlaybackInfoSnapshot PlaybackInfo)
{
    public MediaSessionOrigin Origin { get; init; } = MediaSessionOrigin.Local;
}

/// <summary>Coalescible categories changed on an existing MediaSession.</summary>
[Flags]
public enum MediaSessionChanges
{
    /// <summary>No observable change.</summary>
    None = 0,
    /// <summary>Metadata, artwork key, or source presentation changed.</summary>
    MediaProperties = 1 << 0,
    /// <summary>The timeline sample changed.</summary>
    TimelineProperties = 1 << 1,
    /// <summary>Observed or predicted playback, capabilities, or primary operation changed.</summary>
    PlaybackInfo = 1 << 2,
    /// <summary>The session became available or unavailable.</summary>
    Availability = 1 << 3,
    /// <summary>The logical session retained its identity but received a replacement backend binding.</summary>
    Rebound = 1 << 4,
    /// <summary>The connection identity or effective local/remote treatment changed.</summary>
    Origin = 1 << 5,
}

[Flags]
internal enum MediaServiceChanges
{
    None = 0,
    Status = 1 << 0,
    Availability = 1 << 1,
    Sessions = 1 << 2,
    CurrentSession = 1 << 3,
    Backends = 1 << 4,
}

internal sealed record MediaServiceState(
    long Revision,
    MediaServiceStatus Status,
    MediaControlAvailability Availability,
    ImmutableArray<MediaSession> Sessions,
    MediaSession? CurrentSession)
{
    public ImmutableArray<MediaBackendState> Backends { get; init; } = [];

    public static MediaServiceState Initial { get; } = new(
        0,
        MediaServiceStatus.Stopped,
        MediaControlAvailability.Unavailable,
        [],
        null);
}

internal sealed record MediaSessionSnapshot(
    MediaSessionId Id,
    long BindingGeneration,
    bool IsAvailable,
    MediaPropertiesSnapshot MediaProperties,
    MediaTimelinePropertiesSnapshot TimelineProperties,
    MediaPlaybackInfoSnapshot PlaybackInfo)
{
    public MediaSessionOrigin Origin { get; init; } = MediaSessionOrigin.Local;
}

internal sealed record MediaServiceSnapshot(
    long Revision,
    MediaServiceStatus Status,
    ImmutableArray<MediaSessionSnapshot> Sessions,
    MediaSessionId? CurrentSessionId,
    MediaControlAvailability Availability)
{
    public ImmutableArray<MediaBackendState> Backends { get; init; } = [];

    public static MediaServiceSnapshot Initial { get; } = new(
        0,
        MediaServiceStatus.Stopped,
        [],
        null,
        MediaControlAvailability.Unavailable);
}