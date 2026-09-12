// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Media;

/// <summary>Identifies one image version; obsolete keys must never resolve to a replacement image.</summary>
/// <param name="SessionId">Owning session in the receiving service or backend's namespace.</param>
/// <param name="Version">Image version that must not be reused across replacement bindings of this session.</param>
public readonly record struct MediaArtworkKey(MediaSessionId SessionId, long Version);

/// <summary>Managed artwork content with no stream or native resource for the consumer to dispose.</summary>
/// <param name="ContentType">MIME media type of the encoded image bytes.</param>
/// <param name="Data">Encoded bytes whose backing storage the producer must keep valid and unchanged after publication.</param>
/// <param name="Hash">Optional provider-supplied content hash; algorithm and format are provider-specific.</param>
public sealed record MediaArtworkContent(
    string ContentType,
    ReadOnlyMemory<byte> Data,
    string? Hash);