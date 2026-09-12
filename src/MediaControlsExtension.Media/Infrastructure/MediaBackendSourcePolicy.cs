// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;

namespace JPSoftworks.MediaControlsExtension.Media.Infrastructure;

/// <summary>Replaces one exact application identity supplied by another backend.</summary>
public sealed record MediaBackendSourceClaim(string BackendId, string ApplicationId);

/// <summary>Describes the application identities excluded at one policy revision.</summary>
public sealed class MediaBackendSourcePolicy
{
    public static MediaBackendSourcePolicy Empty { get; } = new(0, []);

    public MediaBackendSourcePolicy(long revision, IEnumerable<string> excludedApplicationIds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        ArgumentNullException.ThrowIfNull(excludedApplicationIds);
        this.Revision = revision;
        this.ExcludedApplicationIds = excludedApplicationIds.ToImmutableHashSet(StringComparer.Ordinal);
        foreach (var applicationId in this.ExcludedApplicationIds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        }
    }

    public long Revision { get; }

    public ImmutableHashSet<string> ExcludedApplicationIds { get; }
}

/// <summary>Applies source exclusions before discovery and at the native execution boundary.</summary>
public interface IMediaSourcePolicyBackend : IMediaBackend
{
    /// <summary>Fences excluded bindings, retires them, and stamps subsequent snapshots with the applied revision.</summary>
    Task ApplySourcePolicyAsync(MediaBackendSourcePolicy policy, CancellationToken cancellationToken);
}