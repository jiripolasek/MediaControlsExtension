// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;

namespace JPSoftworks.MediaControlsExtension.Media;

/// <summary>A Windows application identity, independent of source presentation and command routing.</summary>
public sealed record MediaNativeApplicationIdentity(string ApplicationId, string? ExecutablePath = null);

/// <summary>The registered owner assigned by the composite.</summary>
public sealed record MediaSourceProvider(string Id, string DisplayName);

/// <summary>Provider-localized display text; never used to identify or control a source.</summary>
public sealed record MediaSourceDetail(string Label, string Value);

public sealed record MediaSourceSnapshot(string? DisplayName = null, string? IconPath = null)
{
    public MediaNativeApplicationIdentity? NativeApplication { get; init; }

    public MediaSourceProvider? Provider { get; init; }

    public ImmutableArray<MediaSourceDetail> Details { get; init; } = [];

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