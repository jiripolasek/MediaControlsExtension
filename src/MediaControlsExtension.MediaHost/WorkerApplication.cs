using System.Globalization;
using JPSoftworks.MediaControlsExtension.Media.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.MediaControlsExtension.MediaHost;

internal static class WorkerApplication
{
    private const int StartupFailure = 3;

    private static readonly Action<ILogger, string, Exception?> WatchdogFailure = LoggerMessage.Define<string>(
        LogLevel.Error, new EventId(1, "WorkerWatchdog"), "{Failure}");

    /// <summary>Runs one registered backend; the executable must exit after this method returns.</summary>
    public static async Task<int> RunAsync(string[] args, IReadOnlyDictionary<string, WorkerBackendFactory> factories)
    {
        try
        {
            if (args is not ["--worker", var pipe, var owner, var process, var backendId] ||
                string.IsNullOrWhiteSpace(pipe) ||
                !Guid.TryParse(owner, out var ownerId) ||
                !int.TryParse(process, CultureInfo.InvariantCulture, out var processId) || processId <= 0 ||
                !factories.TryGetValue(backendId, out var factory))
            {
                return StartupFailure;
            }

            return await RunBackendAsync(pipe, ownerId, processId, backendId, factory).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The owner reports launch and handshake failures without a worker UI.
            return StartupFailure;
        }
    }

    private static async Task<int> RunBackendAsync(
        string pipe,
        Guid ownerId,
        int processId,
        string backendId,
        WorkerBackendFactory factory)
    {
        ILoggerFactory? logging = null;
        ProcessMemoryMaintenance? memory = null;
        try
        {
            return await MediaBackendHost.RunAsync(pipe, ownerId, processId, backendId, context =>
                    {
                        logging = CreateLogging(context.Logging);
                        memory = new ProcessMemoryMaintenance(16 * 1024 * 1024,
                            logging.CreateLogger<ProcessMemoryMaintenance>());
                        return factory(context, logging);
                    },
                    reportFailure: error =>
                        WatchdogFailure(logging?.CreateLogger(nameof(MediaBackendHost)) ?? NullLogger.Instance, error,
                            null))
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (memory is not null) { await memory.DisposeAsync().ConfigureAwait(false); }
            }
            finally
            {
                logging?.Dispose();
            }
        }
    }

    private static ILoggerFactory CreateLogging(WorkerLoggingOptions? options)
    {
        try
        {
            return LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(options?.Detailed == true ? LogLevel.Trace : LogLevel.Warning);
                if (options is not null)
                {
                    builder.AddProvider(new WorkerFileLoggerProvider(options));
                }
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            return NullLoggerFactory.Instance;
        }
    }
}