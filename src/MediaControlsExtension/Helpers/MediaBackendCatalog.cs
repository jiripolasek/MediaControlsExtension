// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using JPSoftworks.MediaControlsExtension.Media.Gsmtc;
using JPSoftworks.MediaControlsExtension.Media.Hosting;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.Vlc;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal static class MediaBackendCatalog
{
    public static IReadOnlyDictionary<string, ICommand> CreateConfigurationPages(
        VlcSettings vlcSettings,
        SettingsStore store,
        ILoggerFactory loggerFactory,
        Action configurationChanged)
    {
        var settings = new Settings();
        vlcSettings.AddTo(settings);
        store.Load(settings);
        vlcSettings.Apply();

        var page = new MediaBackendConfigurationPage(
            "vlc",
            Strings.ResourceManager.GetString("Settings_Backend_Vlc_Title", Strings.Culture)!, settings,
            () =>
            {
                store.Save(settings);
                vlcSettings.Apply();
                configurationChanged();
            },
            loggerFactory)
        {
            ValidateInputs = vlcSettings.Validate,
            Commands = [new CommandContextItem(new TestVlcConnectionCommand(vlcSettings.GetOptions, loggerFactory))],
        };

        return new Dictionary<string, ICommand>(StringComparer.Ordinal)
        {
            ["vlc"] = page
        };
    }

    public static MediaBackendRegistry CreateRegistry(Func<VlcConnectionOptions> getVlcOptions, MediaWorkerOwner owner)
    {
        var mediaBackendRegistry = new MediaBackendRegistry();

        mediaBackendRegistry.Register(new(
           "gsmtc",
           Strings.ResourceManager.GetString("Settings_Backend_Gsmtc_Title", Strings.Culture)!,
           Strings.ResourceManager.GetString("Settings_Backend_Gsmtc_Description", Strings.Culture)!,
           static loggerFactory => new GsmtcBackend(
               loggerFactory.CreateLogger<GsmtcBackend>(),
               new GsmtcSourceActivator(loggerFactory.CreateLogger<GsmtcSourceActivator>())),
           EnabledByDefault: false)
        {
            ExclusiveGroup = "windows-media-sessions"
        });

        mediaBackendRegistry.Register(new(
           "gsmtc.worker",
           Strings.ResourceManager.GetString("Settings_Backend_GsmtcWorker_Title", Strings.Culture)!,
           Strings.ResourceManager.GetString("Settings_Backend_GsmtcWorker_Description", Strings.Culture)!,
           loggerFactory => new OutOfProcessMediaBackend(CreateWorkerOptions("gsmtc") with
           {
               ActivateSource = (request, cancellationToken) =>
               {
                   return new GsmtcSourceActivator(loggerFactory.CreateLogger<GsmtcSourceActivator>())
                       .TryActivateAsync(request.ApplicationId, request.MediaTitle, cancellationToken);
               },
           }, owner, loggerFactory), EnabledByDefault: true)
        {
            ExclusiveGroup = "windows-media-sessions"
        });

        mediaBackendRegistry.Register(new(
            "vlc",
            Strings.ResourceManager.GetString("Settings_Backend_Vlc_Title", Strings.Culture)!,
            Strings.ResourceManager.GetString("Settings_Backend_Vlc_Description", Strings.Culture)!,
            _ => new VlcBackend(getVlcOptions,
                Path.Combine(AppContext.BaseDirectory, "Assets", "Providers", "Vlc.svg")))
        {
            ReplacesSources = VlcSourceClaims(getVlcOptions()),
        });

        mediaBackendRegistry.Register(new(
            "itunes",
            Strings.ResourceManager.GetString("Settings_Backend_ITunes_Title", Strings.Culture)!,
            Strings.ResourceManager.GetString("Settings_Backend_ITunes_Description", Strings.Culture)!,
            loggerFactory => new OutOfProcessMediaBackend(CreateWorkerOptions("itunes"), owner, loggerFactory),
            EnabledByDefault: true)
        {
            ReplacesSources = ITunesSourceClaims.ReplacesGsmtcSources,
        });
#if DEBUG || FF_ENABLE_DUMMY_BACKEND
    mediaBackendRegistry..Register(new(
        "dummy.worker",
        Strings.ResourceManager.GetString("Settings_Backend_Dummy_Title", Strings.Culture)!,
        Strings.ResourceManager.GetString("Settings_Backend_Dummy_Description", Strings.Culture)!,
        loggerFactory => new OutOfProcessMediaBackend(CreateWorkerOptions("dummy"), owner, loggerFactory), EnabledByDefault: false));
#endif

        return mediaBackendRegistry;
    }

    private static WorkerOptions CreateWorkerOptions(string backendId)
    {
        return new WorkerOptions(
            Path.Combine(AppContext.BaseDirectory, "MediaHost", "JPSoftworks.MediaControlsExtension.MediaHost.exe"),
            backendId)
        {
            Logging = new(ExtensionHostIdentity.GetLogDirectoryPath(), DetailedLoggingMode.IsEnabled),
        };
    }

    public static ImmutableArray<MediaBackendSourceClaim> VlcSourceClaims(VlcConnectionOptions options)
    {
        return options.IsLocalConnection ? [new("gsmtc", "vlc.exe"), new("gsmtc.worker", "vlc.exe")] : [];
    }
}
