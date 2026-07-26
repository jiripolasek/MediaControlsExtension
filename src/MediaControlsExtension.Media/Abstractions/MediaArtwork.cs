// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Media;

public readonly record struct MediaArtworkKey(MediaSessionId SessionId, long Version);

public sealed record MediaArtworkContent(
    string ContentType,
    ReadOnlyMemory<byte> Data,
    string? Hash);