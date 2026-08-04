// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using JPSoftworks.CommandPalette.Extensions.Toolkit;
using JPSoftworks.CommandPalette.Extensions.Toolkit.Logging.MicrosoftExtensions;

namespace JPSoftworks.MediaControlsExtension;

internal static class Program
{
    [MTAThread]
    public static async Task Main(string[] args)
    {
        var host = ExtensionHostConfiguration.Resolve(
            args,
            new ExtensionHostRunnerParameters
            {
                PublisherMoniker = ExtensionHostIdentity.PublisherMoniker,
                ProductMoniker = ExtensionHostIdentity.ProductMoniker,
            });
        DetailedLoggingMode.InitializeForProcess(host.IsDebug);

        using var loggerFactory = LoggerFactory.Create(builder =>
            builder
                .AddDailyFile(host)
                .AddFilter<DailyFileLoggerProvider>(static (_, level) => DetailedLoggingMode.ShouldWriteToFile(level))
                .AddCommandPalette(host)
                .AddFilter<CommandPaletteLoggerProvider>(static (_, level) => level is >= LogLevel.Critical and < LogLevel.None));

        await ExtensionHostRunner.CreateBuilder(host)
            .AddHostedExtensionFactory(context => new MediaControlsExtension(context.ExtensionDisposedEvent, loggerFactory))
            .UseMicrosoftExtensionsLogging(loggerFactory)
            .RunAsync();
    }
}
