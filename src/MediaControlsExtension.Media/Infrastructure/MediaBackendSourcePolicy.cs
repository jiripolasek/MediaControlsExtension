// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;

namespace JPSoftworks.MediaControlsExtension.Media.Infrastructure;

/// <summary>Replaces one exact application identity supplied by another backend.</summary>
/// <param name="BackendId">Nonblank, case-sensitive ID of the registered backend being replaced.</param>
/// <param name="ApplicationId">Nonblank native application ID, matched with ordinal case-sensitive equality.</param>
public sealed record MediaBackendSourceClaim(string BackendId, string ApplicationId);

/// <summary>Describes the application identities excluded at one policy revision.</summary>
public sealed class MediaBackendSourcePolicy
{
    /// <summary>Gets revision zero with no excluded applications.</summary>
    public static MediaBackendSourcePolicy Empty { get; } = new(0, []);

    /// <summary>Copies exclusions into an immutable ordinal set.</summary>
    /// <param name="revision">Nonnegative policy revision; newer contents require a newer revision.</param>
    /// <param name="excludedApplicationIds">Nonblank native application IDs; duplicates are collapsed.</param>
    /// <exception cref="ArgumentOutOfRangeException">The revision is negative.</exception>
    /// <exception cref="ArgumentNullException">The collection or one of its IDs is null.</exception>
    /// <exception cref="ArgumentException">An application ID is empty or whitespace.</exception>
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

    /// <summary>Gets the policy version acknowledged by subsequent backend snapshots.</summary>
    public long Revision { get; }

    /// <summary>Gets exact native application IDs excluded from discovery and command execution.</summary>
    public ImmutableHashSet<string> ExcludedApplicationIds { get; }
}

/// <summary>Applies source exclusions before discovery and at the native execution boundary.</summary>
public interface IMediaSourcePolicyBackend : IMediaBackend
{
    /// <summary>Rejects new uses of excluded bindings, retires them, and applies the policy to subsequent discovery.</summary>
    /// <param name="policy">Complete replacement policy; revisions cannot decrease or change contents at the same revision.</param>
    /// <param name="cancellationToken">Cancels waiting; exclusions already applied need not be rolled back.</param>
    /// <returns>Completion of policy application; subsequent snapshots must acknowledge its revision.</returns>
    /// <remarks>
    /// May run before startup or overlap observation and commands. Validate exclusions again after internal waits;
    /// already executing work may finish. The composite serializes policy calls for each instance.
    /// </remarks>
    Task ApplySourcePolicyAsync(MediaBackendSourcePolicy policy, CancellationToken cancellationToken);
}