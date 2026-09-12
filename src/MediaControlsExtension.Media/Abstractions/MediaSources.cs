// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;

namespace JPSoftworks.MediaControlsExtension.Media;

/// <summary>A Windows application identity, independent of source presentation and command routing.</summary>
/// <param name="ApplicationId">Nonblank native application ID; used for native lookup and exact source-policy matching.</param>
/// <param name="ExecutablePath">Optional executable path for native application resolution.</param>
public sealed record MediaNativeApplicationIdentity(string ApplicationId, string? ExecutablePath = null);

/// <summary>The registered owner assigned by the composite.</summary>
/// <param name="Id">Stable, case-sensitive registration ID.</param>
/// <param name="DisplayName">Localized provider name.</param>
public sealed record MediaSourceProvider(string Id, string DisplayName);

/// <summary>Provider-localized display text; never used to identify or control a source.</summary>
/// <param name="Label">Nonblank localized label.</param>
/// <param name="Value">Non-null display value; empty values may be hidden by the UI.</param>
public sealed record MediaSourceDetail(string Label, string Value);

/// <summary>Immutable source presentation, compared by value including detail contents and order.</summary>
/// <param name="DisplayName">Optional provider-supplied name; nonblank text takes precedence over native name lookup.</param>
/// <param name="IconPath">Optional host-readable icon path; nonblank text takes precedence over native icon lookup.</param>
public sealed record MediaSourceSnapshot(string? DisplayName = null, string? IconPath = null)
{
    /// <summary>Gets native lookup and source-policy identity, or null for sources without a native application.</summary>
    public MediaNativeApplicationIdentity? NativeApplication { get; init; }

    /// <summary>Gets the registered owner; leaf providers leave it null and the composite replaces it from registration.</summary>
    public MediaSourceProvider? Provider { get; init; }

    /// <summary>Gets ordered display details; the array must be initialized and contain valid, non-null entries.</summary>
    public ImmutableArray<MediaSourceDetail> Details { get; init; } = [];

    /// <summary>Compares all presentation fields and ordered detail values using ordinal string equality.</summary>
    /// <param name="other">Source to compare, or null.</param>
    /// <returns>True when both sources have equal presentation contents.</returns>
    public bool Equals(MediaSourceSnapshot? other)
    {
        return ReferenceEquals(this, other) ||
            (other is not null &&
             this.DisplayName == other.DisplayName &&
             this.IconPath == other.IconPath &&
             this.NativeApplication == other.NativeApplication &&
             this.Provider == other.Provider &&
             this.Details.AsSpan().SequenceEqual(other.Details.AsSpan()));
    }

    /// <summary>Computes a hash from all presentation fields and ordered detail values.</summary>
    /// <returns>A hash consistent with value equality.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(this.DisplayName, StringComparer.Ordinal);
        hash.Add(this.IconPath, StringComparer.Ordinal);
        hash.Add(this.NativeApplication);
        hash.Add(this.Provider);
        foreach (var detail in this.Details.AsSpan())
        {
            hash.Add(detail);
        }

        return hash.ToHashCode();
    }
}