using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using Nerdbank.Streams;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting.Tests;

internal static class HostingTests
{
    public static async Task<int> RunAsync(string resultsDirectory, string? filter = null)
    {
        Directory.CreateDirectory(resultsDirectory);
        var results = new List<string>();
        var failures = 0;
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Backend contract round trip", ContractRoundTripAsync),
            ("Playback confirmation contract round trip", PlaybackConfirmationRoundTripAsync),
            ("Plain backend acknowledges an empty source policy", PlainBackendAsync),
            ("Late artwork is rejected", LateArtworkAsync),
            ("Worker crash fences identities and never replays commands", RecoveryAsync),
            ("Command cancellation keeps the worker usable", CancellationAsync),
            ("Command timeout retires a stuck worker", TimeoutAsync),
            ("Normal disposal exits without forced termination", DisposalAsync),
            ("Broken pipe exits during normal operation", () => BrokenPipeAsync("synthetic")),
            ("Broken pipe exits during hung startup", () => BrokenPipeAsync("hang-start")),
            ("Broken pipe exits during hung cleanup", () => BrokenPipeAsync("hang-cleanup")),
            ("Owner kill terminates its worker", () => OwnerDeathAsync("synthetic", resultsDirectory)),
            ("Owner kill during startup leaves no orphan", () => OwnerDeathAsync("hang-start", resultsDirectory)),
            ("Owner kill terminates a stuck backend", () => OwnerDeathAsync("hang-command", resultsDirectory)),
            ("Oversized frame terminates the worker", () => InvalidMessageAsync(true)),
            ("Protocol mismatch terminates the worker", () => InvalidMessageAsync(false)),
            ("Restart budget stops repeated startup failures", RestartBudgetAsync),
            ("Policy changes during disconnect reach the replacement", PolicyDuringDisconnectAsync),
            ("Owner scope disposal exits workers while its process survives", () => OwnerScopeAsync("synthetic")),
            ("Owner scope disposal exits a worker during startup", () => OwnerScopeAsync("hang-start")),
            ("Owner scope disposal exits a worker with hung cleanup", () => OwnerScopeAsync("hang-cleanup")),
            ("Disposing one owner leaves another owner usable", IndependentOwnersAsync),
            ("Artwork transfers the full 32 MiB limit with control traffic", LargeArtworkAsync),
            ("Oversized artwork is rejected without losing controls", OversizedArtworkAsync),
            ("Source activation is negotiated and runs in its owner", ActivationAsync),
            ("Unmatched activation application never reaches its owner callback", InvalidActivationAsync),
            ("Activation cancellation keeps transport usable", CancelActivationAsync),
            ("Activation timeout fences a late owner result", () => LostActivationAsync(false)),
            ("Worker disconnect fences a late owner activation result", () => LostActivationAsync(true)),
            ("Rebinding rejects a pending activation callback", () => StaleActivationAsync(false)),
            ("Source exclusion rejects a pending activation callback", () => StaleActivationAsync(true))
        };
        tests =
        [
            .. tests, .. ProtocolTests.Cases, .. MemoryMaintenanceTests.Cases,
            .. WorkerApplicationTests.Cases(resultsDirectory), .. DummyBackendTests.Cases
        ];
        tests =
        [
            .. tests, .. ReviewRegressionTests.Cases(resultsDirectory), .. ReviewTimeoutTests.Cases,
            .. ReviewRecoveryTests.Cases
        ];
        tests = [.. tests, .. WorkerLoggingTests.Cases(resultsDirectory), .. WorkerProcessTests.Cases];
        tests = [.. tests, .. ReviewRpcTests.Cases];
        if (filter is not null)
        {
            tests = [.. tests.Where(test => test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))];
            if (tests.Length == 0)
            {
                throw new ArgumentException("No hosting tests match the filter.", nameof(filter));
            }
        }

        foreach (var (name, run) in tests)
        {
            var elapsed = Stopwatch.StartNew();
            try
            {
                await run().ConfigureAwait(false);
                results.Add($"PASS {name} ({elapsed.Elapsed.TotalSeconds:F2}s)");
            }
            catch (Exception ex)
            {
                failures++;
                results.Add($"FAIL {name}: {ex}");
            }

            Console.WriteLine(results[^1]);
        }

        results.Add(
            $"{tests.Length - failures}/{tests.Length} passed. Runtime: {RuntimeInformation.FrameworkDescription}; architecture: {RuntimeInformation.ProcessArchitecture}; dynamic code: {RuntimeFeature.IsDynamicCodeSupported}.");
        await File.WriteAllLinesAsync(Path.Combine(resultsDirectory, "results.txt"), results).ConfigureAwait(false);
        Console.WriteLine(results[^1]);
        return failures == 0 ? 0 : 1;
    }

    internal static MediaBackendCommand Command(MediaBackendSnapshot snapshot, MediaOperation operation) =>
        new(snapshot.Sessions[0].Id, snapshot.Sessions[0].BindingGeneration, operation, []);

    internal static async Task<MediaBackendSnapshot> WaitForSnapshotAsync(
        IMediaBackend backend,
        TimeSpan? timeout = null)
    {
        MediaBackendSnapshot? result = null;
        await EventuallyAsync(async () =>
        {
            result = await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            return result.Connection.Status == MediaConnectionStatus.Connected &&
                   result.Availability == MediaControlAvailability.Available;
        }, timeout).ConfigureAwait(false);
        return result!;
    }

    internal static Task EventuallyAsync(Func<bool> condition, TimeSpan? timeout = null) =>
        EventuallyAsync(() => Task.FromResult(condition()), timeout);

    internal static async Task EventuallyAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var elapsed = Stopwatch.StartNew();
        while (!await condition().ConfigureAwait(false))
        {
            if (elapsed.Elapsed > (timeout ?? TimeSpan.FromSeconds(12)))
            {
                throw new TimeoutException("The expected state did not arrive.");
            }

            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    private static async Task PlaybackConfirmationRoundTripAsync()
    {
        await using var backend = new OutOfProcessMediaBackend(Program.Options("unconfirmed-playback"));
        await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
        var snapshot = await WaitForSnapshotAsync(backend).ConfigureAwait(false);
        Check(snapshot.Sessions[0].Capabilities.HasFlag(MediaCapabilities.TogglePlayback), "Toggle capability was lost in transit.");
        var result = await backend.ExecuteAsync(Command(snapshot, MediaOperation.Play), CancellationToken.None).ConfigureAwait(false);
        Check(result.Status == MediaBackendCommandStatus.Unconfirmed && result.DiagnosticMessage == "Playback could not be confirmed.",
            "Unconfirmed playback result was lost in transit.");
    }

    private static async Task ContractRoundTripAsync()
    {
        await using var backend = new OutOfProcessMediaBackend(Program.Options("synthetic"));
        await backend
            .ApplySourcePolicyAsync(new MediaBackendSourcePolicy(1, ["another.player"]), CancellationToken.None)
            .ConfigureAwait(false);
        await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
        var snapshot = await WaitForSnapshotAsync(backend).ConfigureAwait(false);
        Check(snapshot.SourcePolicyRevision == 1 && snapshot.Sessions.Length == 1,
            "Initial policy/snapshot did not arrive.");
        Check(backend.WorkerPackageFullName == OwnedWorkerProcess.CurrentPackageFullName,
            "The worker has the wrong package identity.");
        Check(snapshot.Sessions[0].Origin.TreatAsLocal, "Local playback became remote.");
        var key = snapshot.Sessions[0].MediaProperties.Artwork!.Value;
        var artwork = await backend.GetArtworkAsync(key, CancellationToken.None).ConfigureAwait(false);
        Check(artwork is not null && artwork.Data.Span.SequenceEqual(new byte[] { 1, 2, 3, 1 }),
            "Artwork bytes changed in transit.");
        Check(
            (await backend.ExecuteAsync(Command(snapshot, MediaOperation.Play), CancellationToken.None)
                .ConfigureAwait(false)).Status == MediaBackendCommandStatus.Completed, "Play failed.");
        await EventuallyAsync(async () =>
            (await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Sessions[0].PlaybackState ==
            MediaPlaybackState.Playing).ConfigureAwait(false);
        backend.InvalidateObservations([
            new MediaBackendObservationRequest(snapshot.Sessions[0].Id, MediaBackendObservationChanges.Playback)
        ]);
        await EventuallyAsync(async () =>
            (await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Sessions[0].MediaProperties
            .Subtitle.Contains("Invalidations: 1", StringComparison.Ordinal)).ConfigureAwait(false);
        await backend.ExecuteAsync(Command(snapshot, MediaOperation.SkipNext), CancellationToken.None)
            .ConfigureAwait(false);
        await EventuallyAsync(async () =>
            (await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Sessions[0].MediaProperties
            .Title == "Next").ConfigureAwait(false);
        Check(await backend.GetArtworkAsync(key, CancellationToken.None).ConfigureAwait(false) is null,
            "Obsolete artwork key was accepted.");
        await backend.ExecuteAsync(Command(snapshot, MediaOperation.SkipPrevious), CancellationToken.None)
            .ConfigureAwait(false);
        await EventuallyAsync(async () =>
            (await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Sessions[0]
            .BindingGeneration > snapshot.Sessions[0].BindingGeneration).ConfigureAwait(false);
        Check(
            (await backend.ExecuteAsync(Command(snapshot, MediaOperation.Play), CancellationToken.None)
                .ConfigureAwait(false)).Status == MediaBackendCommandStatus.SessionGone, "Old binding was accepted.");
        await backend.ApplySourcePolicyAsync(new MediaBackendSourcePolicy(2, ["spike.player"]), CancellationToken.None)
            .ConfigureAwait(false);
        var excluded = await WaitForSnapshotAsync(backend).ConfigureAwait(false);
        Check(excluded.SourcePolicyRevision == 2 && excluded.Sessions.IsEmpty, "Source policy was not enforced.");
        await backend.ApplySourcePolicyAsync(new MediaBackendSourcePolicy(3, []), CancellationToken.None)
            .ConfigureAwait(false);
        await EventuallyAsync(async () =>
                (await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Sessions.Length == 1)
            .ConfigureAwait(false);
        var restored = await WaitForSnapshotAsync(backend).ConfigureAwait(false);
        Check(restored.SourcePolicyRevision == 3 && restored.Sessions.Length == 1,
            "Updated policy did not restore discovery.");
    }

    private static async Task PlainBackendAsync()
    {
        await using var backend = new OutOfProcessMediaBackend(Program.Options("plain"));
        await backend.ApplySourcePolicyAsync(new MediaBackendSourcePolicy(4, []), CancellationToken.None)
            .ConfigureAwait(false);
        await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
        var snapshot = await WaitForSnapshotAsync(backend).ConfigureAwait(false);
        Check(snapshot.SourcePolicyRevision == 4 && snapshot.Sessions.Length == 1,
            "A plain backend could not acknowledge an empty policy.");
    }

    private static async Task OwnerScopeAsync(string behavior)
    {
        await using var owner = new MediaWorkerOwner();
        var backend = new OutOfProcessMediaBackend(Program.Options(behavior), owner);
        await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await EventuallyAsync(() => backend.WorkerProcessId != 0).ConfigureAwait(false);
        using var worker = Process.GetProcessById(backend.WorkerProcessId);
        _ = worker.Handle;
        if (behavior != "hang-start")
        {
            await WaitForSnapshotAsync(backend).ConfigureAwait(false);
        }

        owner.RequestStop();
        var shutdown = owner.DisposeAsync().AsTask();
        do
        {
            var stopped = await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Check(stopped.Sessions.IsEmpty && stopped.Availability == MediaControlAvailability.Unavailable,
                "Stopping the owner did not keep sessions fenced during cleanup.");
            await Task.WhenAny(shutdown, Task.Delay(10)).ConfigureAwait(false);
        } while (!shutdown.IsCompleted);

        await shutdown.ConfigureAwait(false);
        Check(worker.HasExited, "The worker outlived its disposed owner scope.");
        var rejected = false;
        try { _ = new OutOfProcessMediaBackend(Program.Options("synthetic"), owner); }
        catch (ObjectDisposedException) { rejected = true; }

        Check(rejected, "A disposed owner admitted another backend.");
    }

    private static async Task IndependentOwnersAsync()
    {
        await using var first = new MediaWorkerOwner();
        await using var second = new MediaWorkerOwner();
        var a = new OutOfProcessMediaBackend(Program.Options("synthetic"), first);
        var b = new OutOfProcessMediaBackend(Program.Options("synthetic"), second);
        await Task.WhenAll(a.StartAsync(CancellationToken.None), b.StartAsync(CancellationToken.None))
            .ConfigureAwait(false);
        await WaitForSnapshotAsync(a).ConfigureAwait(false);
        var snapshot = await WaitForSnapshotAsync(b).ConfigureAwait(false);
        var survivor = b.WorkerProcessId;
        await first.DisposeAsync().ConfigureAwait(false);
        Check(
            (await b.ExecuteAsync(Command(snapshot, MediaOperation.Play), CancellationToken.None).ConfigureAwait(false))
            .Status == MediaBackendCommandStatus.Completed,
            "Stopping another owner interrupted this worker.");
        Check(b.WorkerProcessId == survivor, "An unrelated worker was restarted.");
    }

    private static async Task LargeArtworkAsync()
    {
        await using var backend = new OutOfProcessMediaBackend(Program.Options("large-artwork"));
        await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
        var snapshot = await WaitForSnapshotAsync(backend).ConfigureAwait(false);
        var read = backend.GetArtworkAsync(snapshot.Sessions[0].MediaProperties.Artwork!.Value, CancellationToken.None)
            .AsTask();
        var control = await backend.ExecuteAsync(Command(snapshot, MediaOperation.Play), CancellationToken.None)
            .ConfigureAwait(false);
        Check(control.Status == MediaBackendCommandStatus.Completed, "Artwork blocked the control channel.");
        var artwork = await read.ConfigureAwait(false);
        Check(artwork?.Data.Length == PipeProtocol.MaximumArtworkBytes, "Full-size artwork was truncated.");
        var data = artwork!.Data;
        for (var index = 0; index < data.Length; index++)
        {
            Check(data.Span[index] == (byte)(index % 251), "Artwork content changed across chunk boundaries.");
        }
    }

    private static async Task OversizedArtworkAsync()
    {
        await using var backend = new OutOfProcessMediaBackend(Program.Options("oversized-artwork"));
        await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
        var snapshot = await WaitForSnapshotAsync(backend).ConfigureAwait(false);
        Check(
            await backend.GetArtworkAsync(snapshot.Sessions[0].MediaProperties.Artwork!.Value, CancellationToken.None)
                .ConfigureAwait(false) is null,
            "Unsupported artwork was accepted.");
        Check(
            (await backend.ExecuteAsync(Command(snapshot, MediaOperation.Play), CancellationToken.None)
                .ConfigureAwait(false)).Status == MediaBackendCommandStatus.Completed,
            "Oversized artwork broke playback control.");
    }

    private static async Task ActivationAsync()
    {
        var calls = 0;
        var ownerPid = Environment.ProcessId;
        await using var backend = new OutOfProcessMediaBackend(Program.Options("activation") with
        {
            ActivateSource = (request, token) =>
            {
                token.ThrowIfCancellationRequested();
                Check(
                    Environment.ProcessId == ownerPid && request.ApplicationId == "spike.player" &&
                    request.CommandId > 0,
                    "Activation was not associated with the owning process and admitted command.");
                calls++;
                return Task.FromResult(true);
            }
        });
        await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
        var snapshot = await WaitForSnapshotAsync(backend).ConfigureAwait(false);
        Check(snapshot.Sessions[0].Capabilities.HasFlag(MediaCapabilities.ActivateSource),
            "Activation capability was not negotiated.");
        Check(
            (await backend.ExecuteAsync(Command(snapshot, MediaOperation.ActivateSource), CancellationToken.None)
                .ConfigureAwait(false)).Status == MediaBackendCommandStatus.Completed,
            "Activation callback failed.");
        Check(calls == 1, "Activation was replayed.");
    }

    private static async Task InvalidActivationAsync()
    {
        var calls = 0;
        await using var backend = new OutOfProcessMediaBackend(Program.Options("invalid-activation") with
        {
            ActivateSource = (_, _) =>
            {
                calls++;
                return Task.FromResult(true);
            }
        });
        await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
        var snapshot = await WaitForSnapshotAsync(backend).ConfigureAwait(false);
        Check(
            (await backend.ExecuteAsync(Command(snapshot, MediaOperation.ActivateSource), CancellationToken.None)
                .ConfigureAwait(false)).Status == MediaBackendCommandStatus.Failed,
            "Unmatched activation was accepted.");
        Check(calls == 0, "An unmatched application reached the owner callback.");
    }

    private static async Task CancelActivationAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var backend = new OutOfProcessMediaBackend(Program.Options("activation") with
        {
            ActivateSource = async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return true;
            }
        });
        await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
        var snapshot = await WaitForSnapshotAsync(backend).ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var activation = backend.ExecuteAsync(Command(snapshot, MediaOperation.ActivateSource), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await cancellation.CancelAsync().ConfigureAwait(false);
        try { await activation.ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        Check(
            (await backend.ExecuteAsync(Command(snapshot, MediaOperation.Play), CancellationToken.None)
                .ConfigureAwait(false)).Status == MediaBackendCommandStatus.Completed,
            "Canceled activation left the transport unusable.");
    }

    private static async Task StaleActivationAsync(bool exclude)
    {
        var calls = 0;
        await using var backend = new OutOfProcessMediaBackend(Program.Options("delayed-activation") with
        {
            ActivateSource = (_, _) =>
            {
                calls++;
                return Task.FromResult(true);
            }
        });
        await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
        var snapshot = await WaitForSnapshotAsync(backend).ConfigureAwait(false);
        var activation = backend.ExecuteAsync(Command(snapshot, MediaOperation.ActivateSource), CancellationToken.None);
        await EventuallyAsync(async () =>
            (await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Sessions[0].MediaProperties
            .Title == "Activating").ConfigureAwait(false);
        if (exclude)
        {
            await backend
                .ApplySourcePolicyAsync(new MediaBackendSourcePolicy(1, ["spike.player"]), CancellationToken.None)
                .ConfigureAwait(false);
        }
        else
        {
            await backend.ExecuteAsync(Command(snapshot, MediaOperation.SkipPrevious), CancellationToken.None)
                .ConfigureAwait(false);
            await EventuallyAsync(async () =>
                (await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Sessions[0]
                .BindingGeneration > snapshot.Sessions[0].BindingGeneration).ConfigureAwait(false);
        }

        Check((await activation.ConfigureAwait(false)).Status != MediaBackendCommandStatus.Completed && calls == 0,
            "A stale binding reached the owner activation callback.");
    }

    private static async Task LostActivationAsync(bool disconnect)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var backend = new OutOfProcessMediaBackend(Program.Options("activation") with
        {
            RequestTimeout = TimeSpan.FromSeconds(1),
            ActivateSource = (_, _) =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                return release.Task;
            }
        });
        try
        {
            await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
            var snapshot = await WaitForSnapshotAsync(backend).ConfigureAwait(false);
            var epoch = backend.WorkerEpoch;
            var activation
                = backend.ExecuteAsync(Command(snapshot, MediaOperation.ActivateSource), CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (disconnect) { backend.DisconnectWorker(); }

            Check((await activation.ConfigureAwait(false)).Status != MediaBackendCommandStatus.Completed,
                "An activation with an uncertain outcome succeeded.");
            await EventuallyAsync(() => backend.WorkerEpoch != epoch).ConfigureAwait(false);
            var replacement = await WaitForSnapshotAsync(backend).ConfigureAwait(false);
            release.TrySetResult(true);
            Check(
                (await backend.ExecuteAsync(Command(snapshot, MediaOperation.Play), CancellationToken.None)
                    .ConfigureAwait(false)).Status == MediaBackendCommandStatus.SessionGone,
                "A command from the old activation lifetime reached its replacement.");
            Check(
                (await backend.ExecuteAsync(Command(replacement, MediaOperation.Play), CancellationToken.None)
                    .ConfigureAwait(false)).Status == MediaBackendCommandStatus.Completed && calls == 1,
                "A late activation result affected the replacement or caused replay.");
        }
        finally
        {
            release.TrySetResult(false);
        }
    }

    private static async Task RestartBudgetAsync()
    {
        await using var backend = new OutOfProcessMediaBackend(Program.Options("unknown") with { MaximumRestarts = 2 });
        await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await EventuallyAsync(async () =>
            (await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Connection.DiagnosticMessage
            ?.Contains("restart budget exhausted", StringComparison.Ordinal) == true).ConfigureAwait(false);
        Check(backend.RestartCount == 2 && backend.WorkerProcessId == 0,
            "The worker continued restarting after its budget was exhausted.");
    }

    private static async Task PolicyDuringDisconnectAsync()
    {
        await using var backend = new OutOfProcessMediaBackend(Program.Options("synthetic") with
        {
            RestartDelay = TimeSpan.FromMilliseconds(500)
        });
        await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await WaitForSnapshotAsync(backend).ConfigureAwait(false);
        var epoch = backend.WorkerEpoch;
        backend.DisconnectWorker();
        await backend.ApplySourcePolicyAsync(new MediaBackendSourcePolicy(9, ["spike.player"]), CancellationToken.None)
            .ConfigureAwait(false);
        await EventuallyAsync(() => backend.WorkerEpoch != epoch).ConfigureAwait(false);
        var snapshot = await WaitForSnapshotAsync(backend).ConfigureAwait(false);
        Check(snapshot.SourcePolicyRevision == 9 && snapshot.Sessions.IsEmpty,
            "The replacement ignored a policy accepted during disconnect.");
    }

    private static async Task LateArtworkAsync()
    {
        await using var backend = new OutOfProcessMediaBackend(Program.Options("slow-artwork"));
        await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
        var snapshot = await WaitForSnapshotAsync(backend).ConfigureAwait(false);
        var request = backend
            .GetArtworkAsync(snapshot.Sessions[0].MediaProperties.Artwork!.Value, CancellationToken.None).AsTask();
        await EventuallyAsync(async () =>
            (await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Sessions[0].MediaProperties
            .Subtitle.Contains("Artwork reads: 1", StringComparison.Ordinal)).ConfigureAwait(false);
        await backend.ExecuteAsync(Command(snapshot, MediaOperation.SkipNext), CancellationToken.None)
            .ConfigureAwait(false);
        await EventuallyAsync(async () =>
            (await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Sessions[0].MediaProperties
            .Title == "Next").ConfigureAwait(false);
        Check(await request.ConfigureAwait(false) is null, "Artwork from an obsolete in-flight request escaped.");
    }

    private static async Task RecoveryAsync()
    {
        await using var backend = new OutOfProcessMediaBackend(Program.Options("hang-command"));
        using var watching = new CancellationTokenSource();
        var watch = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in backend.WatchAsync(watching.Token).ConfigureAwait(false)) { }
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            await backend
                .ApplySourcePolicyAsync(new MediaBackendSourcePolicy(7, ["excluded.player"]), CancellationToken.None)
                .ConfigureAwait(false);
            await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
            var original = await WaitForSnapshotAsync(backend).ConfigureAwait(false);
            var epoch = backend.WorkerEpoch;
            using var worker = Process.GetProcessById(backend.WorkerProcessId);
            var pending = backend.ExecuteAsync(Command(original, MediaOperation.SkipNext), CancellationToken.None);
            await EventuallyAsync(async () =>
                (await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Sessions[0]
                .MediaProperties.Title == "Next").ConfigureAwait(false);
            worker.Kill();
            Check((await pending.ConfigureAwait(false)).Status == MediaBackendCommandStatus.Unavailable,
                "Lost reply was reported as success.");
            await EventuallyAsync(() => backend.WorkerEpoch != epoch && backend.WorkerProcessId != 0)
                .ConfigureAwait(false);
            var replacement = await WaitForSnapshotAsync(backend).ConfigureAwait(false);
            Check(replacement.Sessions[0].Id != original.Sessions[0].Id, "Session ID was reused across workers.");
            Check(replacement.Revision > original.Revision && replacement.SourcePolicyRevision == 7,
                "Revision or policy was lost on recovery.");
            Check(replacement.Sessions[0].MediaProperties.Title == "Initial", "The uncertain command was replayed.");
            Check(
                (await backend.ExecuteAsync(Command(original, MediaOperation.Play), CancellationToken.None)
                    .ConfigureAwait(false)).Status == MediaBackendCommandStatus.SessionGone,
                "Old command reached the replacement.");
            Check(
                await backend
                    .GetArtworkAsync(original.Sessions[0].MediaProperties.Artwork!.Value, CancellationToken.None)
                    .ConfigureAwait(false) is null, "Old artwork reached the replacement.");
            Check(!watch.IsCompleted && backend.RestartCount == 1, "Monitoring ended or unexpected restarts occurred.");
        }
        finally
        {
            await watching.CancelAsync().ConfigureAwait(false);
            await watch.ConfigureAwait(false);
        }
    }

    private static async Task CancellationAsync()
    {
        await using var backend = new OutOfProcessMediaBackend(Program.Options("cancel-command"));
        await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
        var snapshot = await WaitForSnapshotAsync(backend).ConfigureAwait(false);
        var epoch = backend.WorkerEpoch;
        using var cancel = new CancellationTokenSource();
        var command = backend.ExecuteAsync(Command(snapshot, MediaOperation.Stop), cancel.Token);
        await EventuallyAsync(async () =>
            (await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Sessions[0].PlaybackState ==
            MediaPlaybackState.Stopped).ConfigureAwait(false);
        await cancel.CancelAsync().ConfigureAwait(false);
        try
        {
            await command.ConfigureAwait(false);
            throw new InvalidOperationException("Cancellation was ignored.");
        }
        catch (OperationCanceledException) { }

        Check(
            (await backend.ExecuteAsync(Command(snapshot, MediaOperation.Play), CancellationToken.None)
                .ConfigureAwait(false)).Status == MediaBackendCommandStatus.Completed,
            "Cancellation blocked other commands.");
        Check(backend.WorkerEpoch == epoch, "Cancellation unnecessarily replaced the worker.");
    }

    private static async Task TimeoutAsync()
    {
        await using var backend = new OutOfProcessMediaBackend(Program.Options("hang-command") with
        {
            RequestTimeout = TimeSpan.FromMilliseconds(400)
        });
        await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
        var snapshot = await WaitForSnapshotAsync(backend).ConfigureAwait(false);
        var epoch = backend.WorkerEpoch;
        var result = await backend.ExecuteAsync(Command(snapshot, MediaOperation.SkipNext), CancellationToken.None)
            .ConfigureAwait(false);
        Check(result.Status == MediaBackendCommandStatus.Unavailable, "Timeout was reported as success.");
        await EventuallyAsync(() => backend.WorkerEpoch != epoch).ConfigureAwait(false);
        await WaitForSnapshotAsync(backend).ConfigureAwait(false);
    }

    private static async Task DisposalAsync()
    {
        await using var backend = new OutOfProcessMediaBackend(Program.Options("synthetic"));
        await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await WaitForSnapshotAsync(backend).ConfigureAwait(false);
        using var worker = Process.GetProcessById(backend.WorkerProcessId);
        await backend.DisposeAsync().ConfigureAwait(false);
        Check(worker.HasExited && backend.ForcedTerminationCount == 0 && backend.RestartCount == 0,
            "Normal disposal failed or restarted a worker.");
    }

    private static async Task BrokenPipeAsync(string behavior)
    {
        await using var backend = new OutOfProcessMediaBackend(Program.Options(behavior) with { MaximumRestarts = 0 });
        await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
        if (behavior == "hang-start")
        {
            await EventuallyAsync(() => backend.WorkerEpoch != Guid.Empty).ConfigureAwait(false);
        }
        else
        {
            await WaitForSnapshotAsync(backend).ConfigureAwait(false);
        }

        using var worker = Process.GetProcessById(backend.WorkerProcessId);
        backend.DisconnectWorker();
        await EventuallyAsync(() => worker.HasExited, TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        await backend.DisposeAsync().ConfigureAwait(false);
        Check(backend.ForcedTerminationCount == 0, "The worker did not exit itself after pipe loss.");
    }

    private static async Task OwnerDeathAsync(string behavior, string directory)
    {
        var report = Path.Combine(Path.GetFullPath(directory), $"owner-{behavior}-{Guid.NewGuid():N}.txt");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "--lifetime-owner", behavior, report }) { start.ArgumentList.Add(argument); }

        using var owner = Process.Start(start) ?? throw new InvalidOperationException("Failed to start the owner.");
        Process? worker = null;
        try
        {
            var workerId = 0;
            await EventuallyAsync(async () =>
                    File.Exists(report) &&
                    int.TryParse(await File.ReadAllTextAsync(report).ConfigureAwait(false), out workerId))
                .ConfigureAwait(false);
            worker = Process.GetProcessById(workerId);
            _ = worker.SafeHandle;
            Check(!worker.HasExited, "The worker had exited before its owner was killed.");
            owner.Kill(false);
            await owner.WaitForExitAsync().ConfigureAwait(false);
            await EventuallyAsync(() => worker.HasExited, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        finally
        {
            if (!owner.HasExited) { owner.Kill(false); }

            if (worker is not null)
            {
                if (!worker.HasExited) { worker.Kill(); }

                worker.Dispose();
            }
        }
    }

    private static async Task InvalidMessageAsync(bool oversized)
    {
        var owner = Guid.NewGuid();
        var pipeName = $"LOCAL\\JPSoftworks.MediaHostSpike.Invalid.{owner:N}";
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var worker = OwnedWorkerProcess.Start(Environment.ProcessPath!, [
            "--worker", pipeName, owner.ToString("D"),
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture), "synthetic"
        ]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
        await using var multiplexing = await MultiplexingStream
            .CreateAsync(pipe, new MultiplexingStream.Options { ProtocolMajorVersion = 2 }, deadline.Token)
            .ConfigureAwait(false);
        var channel = await multiplexing.OfferChannelAsync("rpc", cancellationToken: deadline.Token)
            .ConfigureAwait(false);
        using var stream = channel.AsStream();
        var payload = Encoding.UTF8.GetBytes(
            $$$"""{"jsonrpc":"2.0","id":1,"method":"InitializeAsync","params":[{"version":99,"owner":"{{{owner:D}}}","backendId":"synthetic","policy":{"revision":0,"excludedApplicationIds":[]}}]}""");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, oversized ? int.MaxValue : payload.Length);
        await stream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
        if (!oversized) { await stream.WriteAsync(payload, deadline.Token).ConfigureAwait(false); }

        await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
        await worker.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
    }

    internal static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}