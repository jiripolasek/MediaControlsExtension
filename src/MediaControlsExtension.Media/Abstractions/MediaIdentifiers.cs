// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Globalization;

namespace JPSoftworks.MediaControlsExtension.Media;

/// <summary>Identifies a logical session within one service lifetime; not a persistent application identifier.</summary>
/// <param name="Value">Opaque backend-assigned value, meaningful only to the service that published it.</param>
public readonly record struct MediaSessionId(long Value)
{
    /// <summary>Formats the session value using invariant culture.</summary>
    /// <returns>The decimal session value.</returns>
    public override string ToString() => this.Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Correlates an accepted command with execution and diagnostics within one service lifetime.</summary>
/// <param name="Value">Service-assigned positive value; zero denotes a rejected submission.</param>
public readonly record struct MediaOperationId(long Value)
{
    /// <summary>Formats the operation value using invariant culture.</summary>
    /// <returns>The decimal operation value.</returns>
    public override string ToString() => this.Value.ToString(CultureInfo.InvariantCulture);
}