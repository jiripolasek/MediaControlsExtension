// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal sealed partial class MediaBackendSettings : IDisposable
{
    private static readonly Action<ILogger, Exception?> RecoveryFailed = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1, nameof(RecoveryFailed)), "Could not recover media source settings.");
    private readonly Lock _gate = new();
    private readonly ILogger _logger;
    private readonly SettingsStore _store;
    private readonly MediaBackendRegistry _registry;
    private readonly Func<SettingsReadResult, SettingsReadResult> _normalize;
    private readonly Dictionary<string, ToggleSetting> _enabled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _diagnostics = new(StringComparer.Ordinal);
    private bool _needsRecovery;
    private CancellationTokenSource? _recoveryStop;
    private bool _disposed;

    public MediaBackendSettings(MediaBackendRegistry registry, SettingsStore store,
        Func<SettingsReadResult, SettingsReadResult>? normalize = null, ILogger? logger = null)
    {
        this._logger = logger ?? NullLogger.Instance;
        this._store = store;
        this._registry = registry;
        this._normalize = normalize ?? (static saved => saved);
        foreach (var registration in registry.Registrations)
        {
            this._enabled.Add(registration.Id, new($"jpsoftworks.mediacontrols.MediaBackends.{registration.Id}.Enabled", registration.EnabledByDefault));
        }
        lock (this._gate)
        {
            this.ApplyValuesUnderLock(store.ReadValues());
            if (this._needsRecovery)
            {
                var stop = new CancellationTokenSource();
                this._recoveryStop = stop;
                _ = Task.Run(() => this.RecoverAsync(stop));
            }
        }
    }

    private void ApplyValuesUnderLock(SettingsReadResult saved)
    {
        saved = this._normalize(saved with { Values = (System.Text.Json.Nodes.JsonObject?)saved.Values?.DeepClone() });
        this._needsRecovery = saved.Status == SettingsReadStatus.Unreadable;
        this._diagnostics.Clear();
        var explicitChoices = new HashSet<string>(StringComparer.Ordinal);
        foreach (var registration in this._registry.Registrations)
        {
            var enabled = this._enabled[registration.Id];
            enabled.Value = registration.EnabledByDefault;
            if (saved.Status == SettingsReadStatus.Unreadable)
            {
                enabled.Value = false;
                this._diagnostics[registration.Id] = Text("UnreadableSettings");
                continue;
            }
            var key = $"jpsoftworks.mediacontrols.MediaBackends.{registration.Id}.Enabled";
            if (saved.Values is { } values && values.TryGetPropertyValue(key, out var value))
            {
                explicitChoices.Add(registration.Id);
                if (bool.TryParse(value?.ToString(), out var selected))
                {
                    enabled.Value = selected;
                }
                else
                {
                    enabled.Value = false;
                    this._diagnostics[registration.Id] = Text("InvalidSavedSelection");
                }
            }
        }

        foreach (var group in this._registry.Registrations.Where(static registration => registration.ExclusiveGroup is not null)
            .GroupBy(static registration => registration.ExclusiveGroup, StringComparer.Ordinal))
        {
            if (saved.Status == SettingsReadStatus.Unreadable) { continue; }
            var explicitEnabled = group.Where(registration => explicitChoices.Contains(registration.Id) && this._enabled[registration.Id].Value).ToArray();
            var invalid = explicitEnabled.Length > 1 || group.Any(registration => this._diagnostics.ContainsKey(registration.Id));
            foreach (var registration in group)
            {
                if (invalid)
                {
                    this._enabled[registration.Id].Value = false;
                    this._diagnostics[registration.Id] = Text("ConflictingSelection");
                }
                else if (explicitEnabled.Length == 1 && registration.Id != explicitEnabled[0].Id)
                {
                    this._enabled[registration.Id].Value = false;
                }
            }
        }

        this._registry.ValidateSelection(this.EnabledIds);
    }

    public event EventHandler<MediaBackendEnabledChangedEventArgs>? EnabledChanged;

    public string? GetDiagnostic(string id)
    {
        lock (this._gate) { return this._diagnostics.GetValueOrDefault(id); }
    }

    public ImmutableArray<string> EnabledIds
    {
        get
        {
            lock (this._gate)
            {
                return [.. this._enabled.Where(static entry => entry.Value.Value).Select(static entry => entry.Key)];
            }
        }
    }

    public void SetEnabled(string id, bool enabled)
    {
        var recovered = false;
        try
        {
            lock (this._gate)
            {
                ObjectDisposedException.ThrowIf(this._disposed, this);
                if (!this._enabled.ContainsKey(id)) { throw new ArgumentException("Unknown media backend.", nameof(id)); }
                if (this._needsRecovery)
                {
                    var saved = this._store.ReadValues();
                    if (saved.Status == SettingsReadStatus.Unreadable)
                    {
                        throw new IOException("Cannot change media sources while settings are unreadable.", saved.Error);
                    }
                    this.ApplyValuesUnderLock(saved);
                    this._recoveryStop?.Cancel();
                    recovered = true;
                }
                this.SetEnabledUnderLock(id, enabled);
            }
        }
        catch
        {
            if (recovered) { this.EnabledChanged?.Invoke(this, new(null, false)); }
            throw;
        }
        this.EnabledChanged?.Invoke(this, new(id, enabled));
    }

    private void SetEnabledUnderLock(string id, bool enabled)
    {
        var registration = this._registry.Registrations.Single(registration => registration.Id == id);
        var affected = this._registry.Registrations.Where(peer => peer.Id == id ||
            registration.ExclusiveGroup is not null && peer.ExclusiveGroup == registration.ExclusiveGroup).ToArray();
        var previous = affected.ToDictionary(static peer => peer.Id, peer => this._enabled[peer.Id].Value, StringComparer.Ordinal);
        var changed = new Settings();
        foreach (var peer in affected)
        {
            var setting = this._enabled[peer.Id];
            if (peer.Id == id)
            {
                setting.Value = enabled;
            }
            else if (enabled)
            {
                setting.Value = false;
            }

            changed.Add(setting);
        }

        try
        {
            this._registry.ValidateSelection(this.EnabledIds);
            this._store.Save(changed);
        }
        catch
        {
            foreach (var peer in affected)
            {
                this._enabled[peer.Id].Value = previous[peer.Id];
            }

            throw;
        }

        foreach (var peer in affected)
        {
            this._diagnostics.Remove(peer.Id);
        }
    }

    private async Task RecoverAsync(CancellationTokenSource stop)
    {
        try
        {
            var delay = TimeSpan.FromMilliseconds(100);
            while (true)
            {
                await Task.Delay(delay, stop.Token).ConfigureAwait(false);
                var saved = this._store.ReadValues(logFailures: false);
                lock (this._gate)
                {
                    if (this._disposed || !this._needsRecovery) { return; }
                    if (saved.Status != SettingsReadStatus.Unreadable)
                    {
                        this.ApplyValuesUnderLock(saved);
                        break;
                    }
                }
                delay = TimeSpan.FromMilliseconds(Math.Min(5000, delay.TotalMilliseconds * 2));
            }
            stop.Token.ThrowIfCancellationRequested();
            this.EnabledChanged?.Invoke(this, new(null, false));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { RecoveryFailed(this._logger, ex); }
        finally
        {
            lock (this._gate)
            {
                this._recoveryStop = null;
                stop.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (this._gate)
        {
            this._disposed = true;
            this._recoveryStop?.Cancel();
        }
    }

    private static string Text(string key) => Strings.ResourceManager.GetString($"Settings_Backend_{key}", Strings.Culture)!;
}

internal sealed class MediaBackendEnabledChangedEventArgs(string? backendId, bool enabled) : EventArgs
{
    /// <summary>The explicit choice, or null when recovering the saved selection.</summary>
    public string? BackendId { get; } = backendId;
    public bool Enabled { get; } = enabled;
}