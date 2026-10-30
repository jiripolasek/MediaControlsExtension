// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using System.Globalization;
using System.Resources;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Browser;

internal sealed record BrowserHealth(string ConnectionId, string Origin, string DisplayName, string ApplicationId,
    TimeSpan HeartbeatAge, bool HasSnapshot)
{
    internal string BrowserName { get; init; } = DisplayName;
    internal string ExecutablePath { get; init; } = string.Empty;
}

internal sealed record BrowserFallbackView(ImmutableArray<BrowserTarget> Targets,
    ImmutableArray<MediaBackendSourceClaim> Claims, MediaBackendConnectionState Connection)
{
    internal bool HasSameContent(BrowserFallbackView other) => this.Targets.SequenceEqual(other.Targets) &&
        this.Claims.SequenceEqual(other.Claims) && this.Connection.Status == other.Connection.Status &&
        this.Connection.DiagnosticMessage == other.Connection.DiagnosticMessage &&
        this.Connection.Connections.SequenceEqual(other.Connection.Connections);
}

internal sealed class BrowserFallbackPolicy(TimeSpan? recoveryDelay = null)
{
    internal static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan FallbackDelay = TimeSpan.FromSeconds(60);
    private static readonly ResourceManager Strings = new("JPSoftworks.MediaControlsExtension.Media.Browser.Resources.Strings", typeof(BrowserFallbackPolicy).Assembly);
    private readonly TimeSpan _recoveryDelay = recoveryDelay ?? TimeSpan.FromSeconds(10);
    private readonly Dictionary<string, Scope> _scopes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BrowserHealth> _profiles = new(StringComparer.Ordinal);
    private Dictionary<string, Observation> _connections = new(StringComparer.Ordinal);
    private ImmutableArray<BrowserTarget> _targets = [];
    private TimeSpan? _lastEvaluation;

    internal BrowserFallbackView Evaluate(TimeSpan now, BrowserCatalog? catalog = null, bool disconnected = false)
    {
        if (this._lastEvaluation is { } last && now - last >= HeartbeatTimeout)
        {
            // Sleep and long scheduling pauses need fresh evidence and a new grace period.
            this._connections.Clear();
            this._targets = [];
            foreach (var scope in this._scopes.Values.Where(scope => scope.Owned)) scope.LastHeartbeat = now;
        }
        this._lastEvaluation = now;

        if (disconnected)
        {
            this._connections.Clear();
            this._targets = [];
        }
        if (catalog is not null)
        {
            var current = new Dictionary<string, Observation>(StringComparer.Ordinal);
            foreach (var health in catalog.Health)
            {
                this._profiles[health.Origin] = health;
                var heartbeat = now - health.HeartbeatAge;
                var valid = health.ApplicationId.Length > 0 && health.HasSnapshot && health.HeartbeatAge >= TimeSpan.Zero &&
                    health.HeartbeatAge < HeartbeatTimeout;
                this._connections.TryGetValue(health.ConnectionId, out var previous);
                var since = valid ? previous is not null && previous.IsHealthy(now) && previous.Health.ApplicationId == health.ApplicationId
                    ? previous.HealthySince ?? now : now : (TimeSpan?)null;
                current.Add(health.ConnectionId, new(health, heartbeat, since));
                if (health.ApplicationId.Length == 0) continue;
                if (!this._scopes.TryGetValue(health.ApplicationId, out var scope))
                    this._scopes.Add(health.ApplicationId, scope = new());
                if (valid && heartbeat > scope.LastHeartbeat) scope.LastHeartbeat = heartbeat;
            }
            this._connections = current;
            this._targets = catalog.Targets;
        }

        var healthy = this._connections.Values.Where(connection => connection.IsHealthy(now)).ToArray();
        foreach (var (applicationId, scope) in this._scopes)
        {
            if (!scope.Owned && healthy.Any(connection => connection.Health.ApplicationId == applicationId &&
                    now - connection.HealthySince >= this._recoveryDelay &&
                    connection.LastHeartbeat - connection.HealthySince >= this._recoveryDelay)) scope.Owned = true;
            if (scope.Owned && now - scope.LastHeartbeat >= FallbackDelay) scope.Owned = false;
        }
        var admitted = healthy.Where(connection => this._scopes[connection.Health.ApplicationId].Owned)
            .Select(connection => connection.Health.ConnectionId).ToHashSet(StringComparer.Ordinal);
        var claims = this._scopes.Where(pair => pair.Value.Owned).OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SelectMany(pair => new MediaBackendSourceClaim[] { new("gsmtc", pair.Key), new("gsmtc.worker", pair.Key) }).ToImmutableArray();
        var connections = this._profiles.Values.Select(profile =>
        {
            var live = this._connections.Values.Any(connection => connection.Health.Origin == profile.Origin &&
                connection.Health.HasSnapshot && now - connection.LastHeartbeat < HeartbeatTimeout &&
                connection.Health.HeartbeatAge >= TimeSpan.Zero);
            if (profile.ApplicationId.Length == 0)
                return new MediaConnectionState(profile.Origin, profile.DisplayName,
                    live ? MediaConnectionStatus.Connected : MediaConnectionStatus.Disconnected, Text("Fallback_Unknown"))
                {
                    ApplicationName = profile.BrowserName, ApplicationPath = profile.ExecutablePath,
                    Source = MediaConnectionSource.Fallback
                };
            var owned = this._scopes[profile.ApplicationId].Owned;
            var otherProfile = healthy.Any(connection => connection.Health.ApplicationId == profile.ApplicationId);
            return new MediaConnectionState(profile.Origin, profile.DisplayName,
                live && owned ? MediaConnectionStatus.Connected : live || owned ? MediaConnectionStatus.Connecting : MediaConnectionStatus.Disconnected,
                Text(live ? owned ? "Fallback_Companion" : "Fallback_Recovery" :
                    owned ? otherProfile ? "Fallback_ProfileUnavailable" : "Fallback_Reconnecting" : "Fallback_Windows"))
            {
                ApplicationId = profile.ApplicationId, ApplicationName = profile.BrowserName, ApplicationPath = profile.ExecutablePath,
                Source = owned ? live ? MediaConnectionSource.Provider : MediaConnectionSource.Reconnecting : MediaConnectionSource.Fallback
            };
        }).ToImmutableArray();
        var status = connections.Any(connection => connection.Status == MediaConnectionStatus.Connected) ? MediaConnectionStatus.Connected :
            connections.Any(connection => connection.Status == MediaConnectionStatus.Connecting) ? MediaConnectionStatus.Connecting : MediaConnectionStatus.Disconnected;
        var diagnostic = connections.Length == 0 ? Text("Fallback_Windows") :
            connections.All(connection => connection.Source == MediaConnectionSource.Provider) ? Text("Fallback_Companion") :
            connections.Any(connection => connection.Source == MediaConnectionSource.Provider) ? Text("Fallback_Partial") :
            connections.First(connection => connection.Status == status).DiagnosticMessage;
        return new([.. this._targets.Where(target => admitted.Contains(target.ConnectionId))], claims,
            new(status, diagnostic) { Connections = connections });
    }

    private static string Text(string name) => Strings.GetString(name, CultureInfo.CurrentUICulture)!;

    private sealed class Scope
    {
        internal bool Owned { get; set; }
        internal TimeSpan LastHeartbeat { get; set; } = TimeSpan.MinValue;
    }

    private sealed record Observation(BrowserHealth Health, TimeSpan LastHeartbeat, TimeSpan? HealthySince)
    {
        internal bool IsHealthy(TimeSpan now) => this.HealthySince is not null && now - this.LastHeartbeat < HeartbeatTimeout;
    }
}