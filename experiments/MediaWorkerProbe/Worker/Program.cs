// ------------------------------------------------------------
//
// Copyright (c) Jiri Polasek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.CommandPalette.Extensions.Toolkit;
using JPSoftworks.CommandPalette.Extensions.Toolkit.Logging.MicrosoftExtensions;
using Microsoft.Extensions.Logging;

namespace JPSoftworks.MediaControlsExtension.MediaWorkerProbe;

internal static class Program
{
    [MTAThread]
    public static async Task Main(string[] args)
    {
        var host = ExtensionHostConfiguration.Resolve(
            args,
            new ExtensionHostRunnerParameters
            {
                PublisherMoniker = "JPSoftworks",
                ProductMoniker = "MediaWorkerProbe",
            });

        using var loggerFactory = LoggerFactory.Create(builder => builder.AddDailyFile(host));

        await ExtensionHostRunner.CreateBuilder(host)
            .AddHostedExtensionFactory(context => new MediaWorkerProbeExtension(
                context.ExtensionDisposedEvent,
                loggerFactory.CreateLogger<MediaWorkerProbeExtension>()))
            .UseMicrosoftExtensionsLogging(loggerFactory)
            .RunAsync();
    }
}