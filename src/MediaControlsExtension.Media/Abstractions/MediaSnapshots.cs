// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;

namespace JPSoftworks.MediaControlsExtension.Media;

public enum MediaServiceStatus
{
    Stopped,
    Starting,
    Ready,
    Degraded,
    Faulted,
}

public enum MediaControlAvailability
{
    Unavailable,
    Available,
    Busy,
    CircuitOpen,
}

public enum MediaPlaybackState
{
    Unknown,
    Closed,
    Opened,
    Changing,
    Stopped,
    Playing,
    Paused,
}

public enum MediaContentType
{
    Unknown,
    Music,
    Video,
    Image,
}

[Flags]
public enum MediaCapabilities
{
    None = 0,
    Play = 1 << 0,
    Pause = 1 << 1,
    Stop = 1 << 2,
    SkipNext = 1 << 3,
    SkipPrevious = 1 << 4,
    ToggleShuffle = 1 << 5,
    ToggleRepeat = 1 << 6,
}

public sealed record MediaApplicationSnapshot(
    string ApplicationId,
    string DisplayName,
    string? ExecutablePath,
    string? IconPath);

public sealed record MediaPropertiesSnapshot(
    MediaApplicationSnapshot Application,
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
    public static MediaPropertiesSnapshot Empty(MediaApplicationSnapshot application) => new(
        application,
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

public sealed record MediaTimelinePropertiesSnapshot(
    TimeSpan StartTime,
    TimeSpan EndTime,
    TimeSpan MinSeekTime,
    TimeSpan MaxSeekTime,
    TimeSpan Position,
    DateTimeOffset? LastUpdatedAt)
{
    public static MediaTimelinePropertiesSnapshot Empty { get; } = new(
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        null);

    public TimeSpan? Duration => this.EndTime > this.StartTime
        ? this.EndTime - this.StartTime
        : null;
}

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
    MediaPlaybackInfoSnapshot PlaybackInfo);

[Flags]
public enum MediaSessionChanges
{
    None = 0,
    MediaProperties = 1 << 0,
    TimelineProperties = 1 << 1,
    PlaybackInfo = 1 << 2,
    Availability = 1 << 3,
    Rebound = 1 << 4,
}

[Flags]
internal enum MediaServiceChanges
{
    None = 0,
    Status = 1 << 0,
    Availability = 1 << 1,
    Sessions = 1 << 2,
    CurrentSession = 1 << 3,
}

internal sealed record MediaServiceState(
    long Revision,
    MediaServiceStatus Status,
    MediaControlAvailability Availability,
    ImmutableArray<MediaSession> Sessions,
    MediaSession? CurrentSession)
{
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
    MediaPlaybackInfoSnapshot PlaybackInfo);

internal sealed record MediaServiceSnapshot(
    long Revision,
    MediaServiceStatus Status,
    ImmutableArray<MediaSessionSnapshot> Sessions,
    MediaSessionId? CurrentSessionId,
    MediaControlAvailability Availability)
{
    public static MediaServiceSnapshot Initial { get; } = new(
        0,
        MediaServiceStatus.Stopped,
        [],
        null,
        MediaControlAvailability.Unavailable);
}