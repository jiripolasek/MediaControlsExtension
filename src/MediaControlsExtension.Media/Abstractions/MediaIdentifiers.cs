// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Globalization;

namespace JPSoftworks.MediaControlsExtension.Media;

/// <summary>
/// Identifies one live media session for the lifetime of the media-service process.
/// It is intentionally not a persistent application identifier.
/// </summary>
public readonly record struct MediaSessionId(long Value)
{
    public override string ToString() => this.Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Correlates a submitted command with its service-side execution and diagnostics.
/// </summary>
public readonly record struct MediaOperationId(long Value)
{
    public override string ToString() => this.Value.ToString(CultureInfo.InvariantCulture);
}