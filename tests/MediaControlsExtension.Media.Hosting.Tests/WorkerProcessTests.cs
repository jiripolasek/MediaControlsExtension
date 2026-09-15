using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting.Tests;

internal static class WorkerProcessTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("A blocked launch does not block peer workers or owner shutdown", SlowLaunchAsync),
        ("Process exit waits support independent cancellation and concurrent callers", ExitWaitsAsync),
        ("Closing the original process handle preserves a live child's exit wait", DisposeDuringWaitAsync),
    ];

    private static async Task SlowLaunchAsync()
    {
        var name = $"MediaWorkerLaunchTest-{Guid.NewGuid():N}";
        using var processRelease = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        using var launchRelease = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Process? lateProcess = null;
        var launches = 0;
        await using var owner = new MediaWorkerOwner((executable, arguments) =>
        {
            var delayed = Interlocked.Increment(ref launches) == 1;
            if (delayed)
            {
                entered.TrySetResult();
                if (!launchRelease.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Launch was not released."); }
            }
            var process = OwnedWorkerProcess.Start(executable, arguments);
            if (delayed)
            {
                lateProcess = Process.GetProcessById(process.ProcessId);
                _ = lateProcess.Handle;
            }
            return process;
        });
        var launching = Task.Run(() => owner.StartWorker(Environment.ProcessPath!, ["--wait-for-release", name]));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            var peer = await Task.Run(() => owner.StartWorker(Environment.ProcessPath!, ["--wait-for-release", name]))
                .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            using var peerProcess = Process.GetProcessById(peer.ProcessId);
            _ = peerProcess.Handle;
            await Task.Run(() => owner.ReleaseWorker(peer)).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await peerProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await Task.Run(owner.RequestStop).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            launchRelease.Set();
            try
            {
                using var unexpected = await launching.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                throw new InvalidOperationException("A late process was registered after shutdown.");
            }
            catch (ObjectDisposedException) { }
            HostingTests.Check(lateProcess is not null, "The blocked process creation was never exercised.");
            await lateProcess!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        finally
        {
            launchRelease.Set();
            processRelease.Set();
            try { using var abandoned = await launching.ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
            lateProcess?.Dispose();
        }
    }

    private static async Task ExitWaitsAsync()
    {
        var name = $"MediaWorkerWaitTest-{Guid.NewGuid():N}";
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        using var process = OwnedWorkerProcess.Start(Environment.ProcessPath!, ["--wait-for-release", name]);
        using var cancellation = new CancellationTokenSource();
        var canceled = process.WaitForExitAsync(cancellation.Token);
        var waiting = Enumerable.Range(0, 8).Select(_ => process.WaitForExitAsync(default)).ToArray();
        cancellation.Cancel();
        try
        {
            await canceled.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            throw new InvalidOperationException("The canceled wait completed successfully.");
        }
        catch (OperationCanceledException) { }
        HostingTests.Check(!process.HasExited && waiting.All(static wait => !wait.IsCompleted), "Cancellation affected the process or another wait.");
        release.Set();
        await Task.WhenAll(waiting).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        await process.WaitForExitAsync(default).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        HostingTests.Check(process.ExitCode == 0, "The released process did not exit normally.");
    }

    private static async Task DisposeDuringWaitAsync()
    {
        var name = $"MediaWorkerDisposeTest-{Guid.NewGuid():N}";
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        using var process = OwnedWorkerProcess.Start(Environment.ProcessPath!, ["--wait-for-release", name]);
        using var observer = Process.GetProcessById(process.ProcessId);
        _ = observer.Handle;
        var waiting = process.WaitForExitAsync(default);
        var original = GetProcessHandle(process);
        // Keep the job open so disposing this handle cannot terminate the child.
        original.Dispose();
        await Task.Delay(150).ConfigureAwait(false);
        HostingTests.Check(original.IsClosed && !observer.HasExited && !waiting.IsCompleted,
            "Closing the original handle killed the child or completed its exit wait.");
        release.Set();
        await waiting.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        HostingTests.Check(observer.HasExited && observer.ExitCode == 0, "The released child did not exit normally.");
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_process")]
    private static extern ref SafeProcessHandle GetProcessHandle(OwnedWorkerProcess process);
}