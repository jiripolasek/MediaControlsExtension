// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal sealed class VlcPasswordSetting : Setting<string>
{
    private readonly Action<string> _savePassword;
    private readonly Action<string>? _reportError;
    private readonly Func<string> _loadPassword;
    private readonly Func<string>? _getConnectionKey;
    private string? _connectionKey;
    private string _password = string.Empty;
    private string? _error;

    public string Password => Volatile.Read(ref this._password);

    public VlcPasswordSetting(string key, string label, string description, string placeholder,
        Func<string> loadPassword, Action<string> savePassword, Action<string>? reportError = null,
        Func<string>? getConnectionKey = null)
        : base(key, label, description, string.Empty)
    {
        this._savePassword = savePassword;
        this._reportError = reportError;
        this._loadPassword = loadPassword;
        this._getConnectionKey = getConnectionKey;
        this.Placeholder = placeholder;
        if (getConnectionKey is null) this.Reload();
    }

    public void Reload()
    {
        var key = this._getConnectionKey?.Invoke() ?? string.Empty;
        if (key == this._connectionKey) return;
        this._connectionKey = key;
        Volatile.Write(ref this._password, string.Empty);
        try
        {
            Volatile.Write(ref this._password, this._loadPassword());
            Volatile.Write(ref this._error, null);
        }
        catch (Exception)
        {
            this._error = Resource("Settings_Vlc_Password_LoadFailed");
        }
    }

    private string Placeholder { get; }

    public override Dictionary<string, object> ToDictionary() => new()
    {
        ["type"] = "Input.Text",
        ["id"] = this.Key,
        ["title"] = this.Label,
        ["label"] = Volatile.Read(ref this._error) is { } error ? $"{error} {this.Description}" : this.Description,
        ["style"] = "password",
        ["value"] = string.Empty,
        ["placeholder"] = this.Placeholder,
    };

    public override void Update(JsonObject payload)
    {
        this.Reload();
        if (payload[this.Key] is JsonValue value && value.TryGetValue<string>(out var password) && password.Length > 0)
        {
            try
            {
                this._savePassword(password);
                Volatile.Write(ref this._password, password);
                Volatile.Write(ref this._error, null);
            }
            catch (Exception)
            {
                var error = Resource("Settings_Vlc_Password_SaveFailed");
                Volatile.Write(ref this._error, error);
                try
                {
                    this._reportError?.Invoke(error);
                }
                catch (Exception)
                {
                    // The form retains the error if the host cannot display a notification.
                }
            }
        }
    }

    // The credential store owns persistence; settings and rendered forms never contain the saved password.
    public override string ToState() => $"\"{this.Key}\": null";

    private static string Resource(string key) => Strings.ResourceManager.GetString(key, Strings.Culture)!;
}