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
    /// <summary>Gets exact source claims held while enabled, including while disconnected or faulted; defaults to empty.</summary>
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
        if (registration.ReplacesSources.IsDefault)
        {
            throw new ArgumentException("Source claims must be an initialized array.", nameof(registration));
        }

        var claims = new HashSet<MediaBackendSourceClaim>();
        foreach (var claim in registration.ReplacesSources)
        {
            ArgumentNullException.ThrowIfNull(claim);
            ArgumentException.ThrowIfNullOrWhiteSpace(claim.BackendId);
            ArgumentException.ThrowIfNullOrWhiteSpace(claim.ApplicationId);
            if (claim.BackendId == registration.Id || !claims.Add(claim))
            {
                throw new ArgumentException("Source claims must be distinct and refer to another backend.", nameof(registration));
            }
        }

        if (this._registrations.Any(existing => string.Equals(existing.Id, registration.Id, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"The media backend '{registration.Id}' is already registered.", nameof(registration));
        }

        this._registrations.Add(registration);
        return this;
    }
}