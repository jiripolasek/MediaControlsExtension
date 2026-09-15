using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting.Tests;

internal static class MemoryMaintenanceTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("Memory maintenance tolerates wrapping and future input timestamps", InputTimestampsAsync),
        ("Memory maintenance disposal interrupts its timer and can be repeated", DisposalAsync),
    ];

    private static Task InputTimestampsAsync()
    {
        if (ProcessMemoryMaintenance.IsUserIdle(1000, 1000) || ProcessMemoryMaintenance.IsUserIdle(1001, 1000) ||
            ProcessMemoryMaintenance.IsUserIdle(1000, 30999) || !ProcessMemoryMaintenance.IsUserIdle(1000, 31000) ||
            !ProcessMemoryMaintenance.IsUserIdle(uint.MaxValue - 20000, 10000))
        {
            throw new InvalidOperationException("Input timestamps did not preserve the thirty-second idle boundary.");
        }

        return Task.CompletedTask;
    }

    private static async Task DisposalAsync()
    {
        await using var maintenance = new ProcessMemoryMaintenance(32 * 1024 * 1024, NullLogger.Instance);
        await Task.WhenAll(maintenance.DisposeAsync().AsTask(), maintenance.DisposeAsync().AsTask())
            .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }
}