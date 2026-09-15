using JPSoftworks.MediaControlsExtension.MediaHost;
using Microsoft.Extensions.Logging;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting.Tests;

internal static class WorkerLoggingTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string directory) =>
    [
        ("Worker logging retains its writer and flushes periodically and on disposal", () => BufferedLoggingAsync(directory)),
        ("Worker log size is bounded by encoded bytes", () => BoundedLoggingAsync(directory)),
        ("Concurrent worker logging and disposal do not lose completed lines or throw", () => ConcurrentLoggingAsync(directory)),
    ];

    private static async Task BufferedLoggingAsync(string directory)
    {
        var logs = Path.Combine(directory, $"buffered-logs-{Guid.NewGuid():N}");
        using var provider = new WorkerFileLoggerProvider(new(logs, true));
        var logger = provider.CreateLogger("test");
        Write(logger, "buffered trace");
        var path = Directory.GetFiles(logs).Single();
        HostingTests.Check(ReadLive(path).Length == 0, "Trace logging flushed each line immediately.");
        try
        {
            using var competingWriter = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            throw new InvalidOperationException("The worker did not retain its log writer.");
        }
        catch (IOException) { }
        await HostingTests.EventuallyAsync(() => ReadLive(path).Contains("buffered trace", StringComparison.Ordinal), TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        Write(logger, "flushed warning", LogLevel.Warning);
        HostingTests.Check(ReadLive(path).Contains("flushed warning", StringComparison.Ordinal), "A warning was left buffered.");
        Write(logger, "final buffered trace");
        provider.Dispose();
        HostingTests.Check(File.ReadAllText(path).Contains("final buffered trace", StringComparison.Ordinal), "Disposal lost the last buffered line.");
        HostingTests.Check(!logger.IsEnabled(LogLevel.Warning), "Disposed logging remained enabled.");
    }

    private static Task BoundedLoggingAsync(string directory)
    {
        var logs = Path.Combine(directory, $"bounded-logs-{Guid.NewGuid():N}");
        using var provider = new WorkerFileLoggerProvider(new(logs, true));
        var logger = provider.CreateLogger("test");
        var message = new string('\u20ac', 16384);
        for (var index = 0; index < 400; index++) { Write(logger, message); }
        HostingTests.Check(!logger.IsEnabled(LogLevel.Trace), "A full log continued formatting trace messages.");
        provider.Dispose();
        var length = new FileInfo(Directory.GetFiles(logs).Single()).Length;
        HostingTests.Check(length is > (4 * 1024 * 1024) - 65536 and <= 4 * 1024 * 1024, "The log limit used characters instead of encoded bytes.");
        return Task.CompletedTask;
    }

    private static async Task ConcurrentLoggingAsync(string directory)
    {
        var logs = Path.Combine(directory, $"concurrent-logs-{Guid.NewGuid():N}");
        using var provider = new WorkerFileLoggerProvider(new(logs, true));
        var logger = provider.CreateLogger("test");
        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (var line = 0; line < 100; line++) { Write(logger, $"worker {worker} line {line}"); }
        }))).ConfigureAwait(false);
        provider.Dispose();
        HostingTests.Check(File.ReadAllLines(Directory.GetFiles(logs).Single()).Length == 400, "Concurrent logging lost or interleaved complete lines.");
        await Task.WhenAll(Task.Run(provider.Dispose), Task.Run(() => Write(logger, "after disposal"))).ConfigureAwait(false);
        HostingTests.Check(File.ReadAllLines(Directory.GetFiles(logs).Single()).Length == 400, "Disposal allowed later writes.");
    }

    private static string ReadLive(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void Write(ILogger logger, string message, LogLevel level = LogLevel.Trace) =>
        logger.Log(level, new EventId(1), message, null, static (state, _) => state);
}