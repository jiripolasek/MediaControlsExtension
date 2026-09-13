// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Media;

/// <summary>Identifies a configured connection and its effective playback behavior.</summary>
/// <param name="ConnectionId">Stable, nonblank provider-local connection ID; never a display name or binding epoch.</param>
/// <param name="TreatAsLocal">Selects local grouping and eligibility for automatic selection, cycling, and automatic pausing.</param>
/// <remarks>This behavior does not establish a local native application identity or a source replacement claim.</remarks>
public sealed record MediaSessionOrigin(string ConnectionId, bool TreatAsLocal = true)
{
    /// <summary>Gets the default origin for a provider exposing local sessions.</summary>
    public static MediaSessionOrigin Local { get; } = new("local");
}