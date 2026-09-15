// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.CommandPalette.Extensions.Toolkit;
using JPSoftworks.CommandPalette.Extensions.Toolkit.Logging.MicrosoftExtensions;
using JPSoftworks.MediaControlsExtension.Media.Hosting;
using System.Collections.Concurrent;

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

        await using var memory = new ProcessMemoryMaintenance(32 * 1024 * 1024, loggerFactory.CreateLogger<ProcessMemoryMaintenance>());
        var owners = new ConcurrentBag<MediaWorkerOwner>();
        try
        {
            await ExtensionHostRunner.CreateBuilder(host)
                .AddHostedExtensionFactory(context =>
                {
                    var owner = new MediaWorkerOwner();
                    owners.Add(owner);
                    return new MediaControlsExtension(context.ExtensionDisposedEvent, loggerFactory, owner);
                })
                .UseMicrosoftExtensionsLogging(loggerFactory)
                .RunAsync();
        }
        finally
        {
            try
            {
                await Task.WhenAll(owners.Select(static owner => owner.DisposeAsync().AsTask())).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ExtensionLog.UnexpectedError(loggerFactory.CreateLogger(nameof(Program)), ex);
            }
        }
    }
}