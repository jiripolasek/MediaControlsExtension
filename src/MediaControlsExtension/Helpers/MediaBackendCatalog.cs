// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Gsmtc;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal static class MediaBackendCatalog
{
    public static MediaBackendRegistry CreateRegistry() => new MediaBackendRegistry().Register(new(
        "gsmtc",
        Strings.ResourceManager.GetString("Settings_Backend_Gsmtc_Title", Strings.Culture)!,
        Strings.ResourceManager.GetString("Settings_Backend_Gsmtc_Description", Strings.Culture)!,
        static loggerFactory => new GsmtcBackend(
            loggerFactory.CreateLogger<GsmtcBackend>(),
            new GsmtcSourceActivator(loggerFactory.CreateLogger<GsmtcSourceActivator>())),
        EnabledByDefault: true));
}