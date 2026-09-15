using System.Diagnostics;
using JPSoftworks.MediaControlsExtension.Media.Gsmtc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting.Tests;

internal static partial class NativeChecks
{
    public static async Task RunMemoryAsync(string directory, int cycles)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cycles);
        Directory.CreateDirectory(directory);
        using var memoryLog = new NativeCheckLogger(directory, LogLevel.Debug);
        await using var maintenance = new ProcessMemoryMaintenance(16 * 1024 * 1024, memoryLog);
        var timer = Stopwatch.StartNew();
        var rows = new List<string>
        {
            "sample,phase,elapsed_ms,private_bytes,working_set,managed_bytes,gc_heap_bytes,gc_committed_bytes,gc_fragmented_bytes,gen0,gen1,gen2,total_allocated_bytes",
        };
        await File.WriteAllLinesAsync(Path.Combine(directory, "gc-configuration.txt"),
            GC.GetConfigurationVariables().Select(static pair => $"{pair.Key}={pair.Value}")).ConfigureAwait(false);
        var title = $"Media memory acceptance {Guid.NewGuid():N}";
        using var player = new PlayerProcess(title);
        var activator = new TestSourceActivator(title);
        await using var backend = new GsmtcBackend(NullLogger<GsmtcBackend>.Instance, activator);
        await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
        for (var index = 0; index < cycles; index++)
        {
            if (index > 0 && index % 10 == 0) { await player.RestartAsync().ConfigureAwait(false); }
            await VerifyCommandsAsync(backend, player.Title, activator, directory).ConfigureAwait(false);
            if (index % 25 == 0) { Record(index, "churn"); }
        }

        Record(cycles, "before-collection");
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        await Task.Run(GC.WaitForPendingFinalizers).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        Record(cycles, "after-collection");
        await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Record(cycles, "after-idle");
        ProcessMemoryMaintenance.TrimCurrentProcess();
        Record(cycles, "after-trim");
        await VerifyCommandsAsync(backend, player.Title, activator, directory).ConfigureAwait(false);
        Record(cycles, "after-resumed-controls");
        await File.WriteAllTextAsync(Path.Combine(directory, "memory-result.txt"),
            FormattableString.Invariant($"PASS {cycles} native GSMTC samples; {timer.Elapsed.TotalSeconds:F2} seconds; controls remain usable after full collection and working-set trim.")).ConfigureAwait(false);

        void Record(int sample, string phase)
        {
            using var process = Process.GetCurrentProcess();
            var info = GC.GetGCMemoryInfo();
            rows.Add(FormattableString.Invariant($"{sample},{phase},{timer.Elapsed.TotalMilliseconds:F2},{process.PrivateMemorySize64},{process.WorkingSet64},{GC.GetTotalMemory(false)},{info.HeapSizeBytes},{info.TotalCommittedBytes},{info.FragmentedBytes},{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)},{GC.GetTotalAllocatedBytes()}"));
            File.WriteAllLines(Path.Combine(directory, "memory.csv"), rows);
        }
    }
}