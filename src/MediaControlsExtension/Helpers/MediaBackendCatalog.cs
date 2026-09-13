// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Gsmtc;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.Vlc;
using System.Collections.Immutable;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal static class MediaBackendCatalog
{
    public static IReadOnlyDictionary<string, ICommand> CreateConfigurationPages(
        VlcSettings vlcSettings, SettingsStore store, ILoggerFactory loggerFactory, Action configurationChanged)
    {
        var settings = new Settings();
        vlcSettings.AddTo(settings);
        store.Load(settings);
        vlcSettings.Apply();
        var page = new MediaBackendConfigurationPage("vlc",
            Strings.ResourceManager.GetString("Settings_Backend_Vlc_Title", Strings.Culture)!, settings,
            () =>
            {
                store.Save(settings);
                vlcSettings.Apply();
                configurationChanged();
            }, loggerFactory)
        {
            ValidateInputs = vlcSettings.Validate,
            Commands = [new CommandContextItem(new TestVlcConnectionCommand(vlcSettings.GetOptions, loggerFactory))],
        };
        return new Dictionary<string, ICommand>(StringComparer.Ordinal) { ["vlc"] = page };
    }

    public static MediaBackendRegistry CreateRegistry(Func<VlcConnectionOptions> getVlcOptions) => new MediaBackendRegistry().Register(new(
        "gsmtc",
        Strings.ResourceManager.GetString("Settings_Backend_Gsmtc_Title", Strings.Culture)!,
        Strings.ResourceManager.GetString("Settings_Backend_Gsmtc_Description", Strings.Culture)!,
        static loggerFactory => new GsmtcBackend(
            loggerFactory.CreateLogger<GsmtcBackend>(),
            new GsmtcSourceActivator(loggerFactory.CreateLogger<GsmtcSourceActivator>())),
        EnabledByDefault: true)).Register(new(
        "vlc",
        Strings.ResourceManager.GetString("Settings_Backend_Vlc_Title", Strings.Culture)!,
        Strings.ResourceManager.GetString("Settings_Backend_Vlc_Description", Strings.Culture)!,
        _ => new VlcBackend(getVlcOptions, Path.Combine(AppContext.BaseDirectory, "Assets", "Providers", "Vlc.svg")))
    {
        ReplacesSources = VlcSourceClaims(getVlcOptions()),
    });

    public static ImmutableArray<MediaBackendSourceClaim> VlcSourceClaims(VlcConnectionOptions options) =>
        options.IsLocalConnection ? [new("gsmtc", "vlc.exe")] : [];
}