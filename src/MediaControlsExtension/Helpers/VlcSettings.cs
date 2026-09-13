// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using JPSoftworks.MediaControlsExtension.Media.Vlc;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Security.Credentials;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal sealed class VlcSettings
{
    private const string CredentialResource = "JPSoftworks.MediaControlsExtension.Vlc";
    private const string LegacyCredentialUser = "http";
    private readonly VlcEndpointSetting _endpoint = new();
    private readonly VlcTreatmentSetting _treatment = new();
    private readonly VlcPasswordSetting _password;
    private VlcConnectionOptions _options = new();

    public VlcSettings() : this(LoadPassword, SavePassword, static message => new ToastStatusMessage(message).Show())
    {
    }

    internal VlcSettings(Func<string, string> loadPassword, Action<string, string> savePassword, Action<string>? reportError = null)
    {
        this._password = new("jpsoftworks.mediacontrols.MediaBackends.vlc.Password",
            Resource("Settings_Vlc_Password_Title"), Resource("Settings_Vlc_Password_Description"),
            Resource("Settings_Vlc_Password_Placeholder"),
            () =>
            {
                var key = this.CredentialKey;
                if (key.Length == 0) return string.Empty;
                var password = loadPassword(key);
                if (password.Length == 0 && key == this._endpoint.LegacyCredentialEndpoint)
                {
                    password = loadPassword(LegacyCredentialUser);
                    if (password.Length > 0) savePassword(key, password);
                }

                return password;
            }, password => savePassword(this.CredentialKey, password), reportError, () => this.CredentialKey);
    }

    private string CredentialKey => VlcConnectionOptions.ParseEndpoint(this._endpoint.Value)?.AbsoluteUri ?? string.Empty;

    public VlcConnectionOptions GetOptions() => Volatile.Read(ref this._options);

    public void AddTo(Settings settings)
    {
        settings.Add(new SettingsGroupHeader(
            "jpsoftworks.mediacontrols.Layout.Vlc", Resource("Settings_Group_Vlc")));
        settings.Add(new TextBlockSetting(
            "jpsoftworks.mediacontrols.Layout.VlcHelp", Resource("Settings_Vlc_Help"), isSubtle: true));
        settings.Add(this._endpoint);
        settings.Add(this._password);
        settings.Add(this._treatment);
    }

    public string? Validate(string inputs)
    {
        var payload = JsonNode.Parse(inputs)!.AsObject();
        return VlcConnectionOptions.ParseEndpoint(this._endpoint.ReadValue(payload)) is null
            ? Resource("Settings_Vlc_Endpoint_Invalid") : null;
    }

    public void Apply()
    {
        this._password.Reload();
        var options = new VlcConnectionOptions(Password: this._password.Password) { Endpoint = this._endpoint.Value };
        options = this._treatment.Value switch
        {
            "local" => options with { TreatAsLocal = true },
            "remote" => options with { TreatAsLocal = false },
            _ => options,
        };
        Volatile.Write(ref this._options, options);
    }

    private static string LoadPassword(string key)
    {
        try
        {
            var credential = new PasswordVault().Retrieve(CredentialResource, key);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070490))
        {
            return string.Empty;
        }
    }

    private static void SavePassword(string key, string password) =>
        new PasswordVault().Add(new PasswordCredential(CredentialResource, key, password));

    private static string Resource(string key) => Strings.ResourceManager.GetString(key, Strings.Culture)!;
}