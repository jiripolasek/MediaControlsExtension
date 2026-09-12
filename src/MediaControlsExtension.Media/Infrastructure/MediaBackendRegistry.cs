// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace JPSoftworks.MediaControlsExtension.Media.Infrastructure;

/// <summary>Describes a provider without creating its runtime instance.</summary>
public sealed record MediaBackendRegistration(
    string Id,
    string DisplayName,
    string Description,
    Func<ILoggerFactory, IMediaBackend> CreateBackend,
    bool EnabledByDefault = false)
{
    public ImmutableArray<MediaBackendSourceClaim> ReplacesSources { get; init; } = [];
}

/// <summary>Collects provider factories before constructing the composite and its settings.</summary>
public sealed class MediaBackendRegistry
{
    private readonly List<MediaBackendRegistration> _registrations = [];

    public ImmutableArray<MediaBackendRegistration> Registrations => [.. this._registrations];

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