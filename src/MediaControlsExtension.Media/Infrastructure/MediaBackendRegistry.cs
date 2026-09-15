// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace JPSoftworks.MediaControlsExtension.Media.Infrastructure;

/// <summary>Describes a provider without creating its runtime instance.</summary>
/// <param name="Id">Stable, nonblank, case-sensitive registration and settings key.</param>
/// <param name="DisplayName">Nonblank localized provider name.</param>
/// <param name="Description">Localized settings description.</param>
/// <param name="CreateBackend">Factory returning a fresh instance per enable; the composite owns its disposal.</param>
/// <param name="EnabledByDefault">Initial selection when no explicit enabled-ID list is supplied.</param>
public sealed record MediaBackendRegistration(
    string Id,
    string DisplayName,
    string Description,
    Func<ILoggerFactory, IMediaBackend> CreateBackend,
    bool EnabledByDefault = false)
{
    /// <summary>Gets an optional ordinal group allowing zero or one selected provider.</summary>
    public string? ExclusiveGroup { get; init; }

    /// <summary>Gets initial source claims held while enabled, including while disconnected or faulted; defaults to empty.</summary>
    /// <remarks>The composite can replace these claims when the provider's configuration changes.</remarks>
    public ImmutableArray<MediaBackendSourceClaim> ReplacesSources { get; init; } = [];
}

/// <summary>Collects provider factories before constructing the composite and its settings.</summary>
/// <remarks>Configure on one thread before consumption. Registration does not instantiate providers.</remarks>
public sealed class MediaBackendRegistry
{
    private readonly List<MediaBackendRegistration> _registrations = [];

    /// <summary>Gets a copied registration list in insertion order.</summary>
    public ImmutableArray<MediaBackendRegistration> Registrations => [.. this._registrations];

    /// <summary>Adds a descriptor with a unique ordinal ID and valid source claims.</summary>
    /// <param name="registration">Descriptor to register; claims must be distinct and refer to another backend.</param>
    /// <returns>This registry for chained registration.</returns>
    /// <exception cref="ArgumentNullException">The descriptor, factory, claim, or a required string is null.</exception>
    /// <exception cref="ArgumentException">An ID or name is blank, the ID is duplicate, or source claims are invalid.</exception>
    /// <remarks>Claim targets may be registered later; the composite validates their existence and enabled-owner conflicts.</remarks>
    public MediaBackendRegistry Register(MediaBackendRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.DisplayName);
        ArgumentNullException.ThrowIfNull(registration.CreateBackend);
        if (registration.ExclusiveGroup is { } group)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(group);
        }
        ValidateSourceClaims(registration.Id, registration.ReplacesSources);

        if (this._registrations.Any(existing => string.Equals(existing.Id, registration.Id, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"The media backend '{registration.Id}' is already registered.", nameof(registration));
        }

        this._registrations.Add(registration);
        return this;
    }

    /// <summary>Validates registered IDs and exclusive groups without creating providers.</summary>
    /// <exception cref="ArgumentException">An ID is unknown.</exception>
    /// <exception cref="InvalidOperationException">Multiple selected providers belong to one exclusive group.</exception>
    public void ValidateSelection(IEnumerable<string> enabledIds)
    {
        ArgumentNullException.ThrowIfNull(enabledIds);
        ValidateSelection(this.Registrations, enabledIds.ToHashSet(StringComparer.Ordinal));
    }

    internal static void ValidateSelection(ImmutableArray<MediaBackendRegistration> registrations, HashSet<string> enabledIds)
    {
        foreach (var id in enabledIds)
        {
            if (!registrations.Any(registration => registration.Id == id))
            {
                throw new ArgumentException($"Unknown media backend '{id}'.", nameof(enabledIds));
            }
        }

        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            if (enabledIds.Contains(registration.Id) && registration.ExclusiveGroup is { } group && !groups.Add(group))
            {
                throw new InvalidOperationException($"Multiple selected media backends belong to exclusive group '{group}'.");
            }
        }
    }

    internal static void ValidateSourceClaims(string backendId, ImmutableArray<MediaBackendSourceClaim> sourceClaims)
    {
        if (sourceClaims.IsDefault)
        {
            throw new ArgumentException("Source claims must be an initialized array.", nameof(sourceClaims));
        }

        var claims = new HashSet<MediaBackendSourceClaim>();
        foreach (var claim in sourceClaims)
        {
            ArgumentNullException.ThrowIfNull(claim);
            ArgumentException.ThrowIfNullOrWhiteSpace(claim.BackendId);
            ArgumentException.ThrowIfNullOrWhiteSpace(claim.ApplicationId);
            if (claim.BackendId == backendId || !claims.Add(claim))
            {
                throw new ArgumentException("Source claims must be distinct and refer to another backend.", nameof(sourceClaims));
            }
        }
    }
}