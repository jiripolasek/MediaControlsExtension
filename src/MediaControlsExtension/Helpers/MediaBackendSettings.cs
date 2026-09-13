// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal sealed class MediaBackendSettings
{
    private readonly Lock _gate = new();
    private readonly SettingsStore _store;
    private readonly Settings _settings = new();
    private readonly Dictionary<string, ToggleSetting> _enabled = new(StringComparer.Ordinal);

    public MediaBackendSettings(MediaBackendRegistry registry, SettingsStore store)
    {
        this._store = store;
        foreach (var registration in registry.Registrations)
        {
            var enabled = new ToggleSetting($"jpsoftworks.mediacontrols.MediaBackends.{registration.Id}.Enabled", registration.EnabledByDefault);
            this._enabled.Add(registration.Id, enabled);
            this._settings.Add(enabled);
        }

        this._store.Load(this._settings);
    }

    public event EventHandler? EnabledChanged;

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
        lock (this._gate)
        {
            var setting = this._enabled[id];
            var previous = setting.Value;
            setting.Value = enabled;
            try
            {
                this._store.Save(this._settings);
            }
            catch
            {
                setting.Value = previous;
                throw;
            }
        }

        this.EnabledChanged?.Invoke(this, EventArgs.Empty);
    }
}