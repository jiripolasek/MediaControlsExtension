using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting.Tests;

internal static class ReviewRecoveryTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("Transient snapshot errors recover in the same worker and preserve sessions", SnapshotRecoveryAsync),
        ("Initial snapshot errors recover within the startup budget", StartupSnapshotRecoveryAsync),
        ("Persistent snapshot errors replace the poisoned worker and fence old commands",
            PersistentSnapshotRecoveryAsync),
        ("Repeated persistent failures exhaust the normal restart budget", PersistentSnapshotBudgetAsync),
        ("Successful snapshots reset the consecutive failure window", SnapshotWindowResetAsync),
        ("Only continuously healthy snapshots reset the restart budget", SnapshotHealthAsync),
        ("Owner cancellation returns while a partial request finishes safely", OwnerFrameCancellationAsync)
    ];

    private static async Task SnapshotRecoveryAsync()
    {
        await using var backend
            = new OutOfProcessMediaBackend(Program.Options("transient-read-failure") with { MaximumRestarts = 0 });
        var registry
            = new MediaBackendRegistry().Register(new MediaBackendRegistration("worker", "Worker", "", _ => backend,
                true));
        await using var composite = new CompositeMediaBackend(registry);
        await composite.StartAsync(default).ConfigureAwait(false);
        MediaBackendSnapshot snapshot = null!;
        await HostingTests.EventuallyAsync(async () =>
        {
            snapshot = await composite.ReadSnapshotAsync(default).ConfigureAwait(false);
            return snapshot.Sessions.Length == 1 && snapshot.Sessions[0].IsAvailable;
        }).ConfigureAwait(false);
        var original = snapshot.Sessions[0];
        var epoch = backend.WorkerEpoch;
        var pid = backend.WorkerProcessId;
        var result = await composite.ExecuteAsync(HostingTests.Command(snapshot, MediaOperation.Stop), default)
            .ConfigureAwait(false);
        HostingTests.Check(result.Status == MediaBackendCommandStatus.Completed, "The trigger command failed.");
        await HostingTests.EventuallyAsync(async () =>
        {
            snapshot = await composite.ReadSnapshotAsync(default).ConfigureAwait(false);
            return snapshot.Backends[0].Status == MediaBackendLifecycleStatus.Faulted;
        }).ConfigureAwait(false);
        HostingTests.Check(
            snapshot.Sessions.Length == 1 && !snapshot.Sessions[0].IsAvailable &&
            snapshot.Sessions[0].Id == original.Id,
            "A recoverable error removed the captured session instead of making it unavailable.");
        HostingTests.Check(
            (await composite.ExecuteAsync(HostingTests.Command(snapshot, MediaOperation.Play), default)
                .ConfigureAwait(false)).Status != MediaBackendCommandStatus.Completed,
            "A command bypassed the observation failure.");
        await HostingTests.EventuallyAsync(async () =>
        {
            snapshot = await composite.ReadSnapshotAsync(default).ConfigureAwait(false);
            return snapshot.Sessions.Length == 1 && snapshot.Sessions[0].IsAvailable &&
                   snapshot.Backends[0].Status == MediaBackendLifecycleStatus.Ready;
        }).ConfigureAwait(false);
        HostingTests.Check(backend.WorkerEpoch == epoch && backend.WorkerProcessId == pid && backend.RestartCount == 0,
            "Transient read errors consumed a restart or replaced the worker.");
        HostingTests.Check(
            snapshot.Sessions[0].Id == original.Id &&
            snapshot.Sessions[0].BindingGeneration == original.BindingGeneration,
            "Recovery replaced a surviving session identity.");
        HostingTests.Check(
            (await composite.ExecuteAsync(HostingTests.Command(snapshot, MediaOperation.Play), default)
                .ConfigureAwait(false)).Status == MediaBackendCommandStatus.Completed,
            "Recovered controls remained unavailable.");
    }

    private static async Task StartupSnapshotRecoveryAsync()
    {
        await using var backend = new OutOfProcessMediaBackend(Program.Options("startup-read-failure") with
        {
            MaximumRestarts = 0, StartupTimeout = TimeSpan.FromSeconds(5)
        });
        await backend.StartAsync(default).ConfigureAwait(false);
        var errors = 0;
        await HostingTests.EventuallyAsync(async () =>
        {
            try { return (await backend.ReadSnapshotAsync(default).ConfigureAwait(false)).Sessions.Length == 1; }
            catch (IOException)
            {
                errors++;
                return false;
            }
        }).ConfigureAwait(false);
        HostingTests.Check(errors > 0 && backend.RestartCount == 0 && backend.WorkerProcessId != 0,
            "Initial read failures did not recover in the first worker.");
    }

    private static async Task OwnerFrameCancellationAsync()
    {
        var id = $"review-partial-request-{Guid.NewGuid():N}";
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, id);
        await using var backend = new OutOfProcessMediaBackend(Program.Options(id) with { MaximumRestarts = 0 });
        await backend.StartAsync(default).ConfigureAwait(false);
        await HostingTests.WaitForSnapshotAsync(backend).ConfigureAwait(false);
        var epoch = backend.WorkerEpoch;
        using var caller = new CancellationTokenSource();
        var policy = backend.ApplySourcePolicyAsync(new MediaBackendSourcePolicy(1, [new string('x', 2 * 1024 * 1024)]),
            caller.Token);
        try
        {
            await HostingTests.EventuallyAsync(async () =>
                (await backend.ReadSnapshotAsync(default).ConfigureAwait(false)).Sessions[0].MediaProperties.Title ==
                "Partial frame").ConfigureAwait(false);
            caller.Cancel();
            try
            {
                await policy.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                throw new InvalidOperationException("Caller cancellation did not cancel its wait.");
            }
            catch (OperationCanceledException) { }

            HostingTests.Check(backend.WorkerEpoch == epoch && backend.RestartCount == 0,
                "Caller cancellation closed the worker.");
        }
        finally { release.Set(); }

        await HostingTests.EventuallyAsync(async () =>
            (await backend.ReadSnapshotAsync(default).ConfigureAwait(false)).Sessions[0].MediaProperties.Title ==
            "Frame received").ConfigureAwait(false);
        var snapshot = await backend.ReadSnapshotAsync(default).ConfigureAwait(false);
        HostingTests.Check(
            (await backend.ExecuteAsync(HostingTests.Command(snapshot, MediaOperation.Play), default)
                .ConfigureAwait(false)).Status == MediaBackendCommandStatus.Completed,
            "The completed frame left controls unusable.");
        snapshot = await backend.ReadSnapshotAsync(default).ConfigureAwait(false);
        HostingTests.Check(
            snapshot.Sessions[0].MediaProperties.Title == "Frame received" && backend.WorkerEpoch == epoch &&
            backend.RestartCount == 0,
            "A stale policy failure affected the current snapshot.");
    }

    private static async Task PersistentSnapshotRecoveryAsync()
    {
        await using var backend = new OutOfProcessMediaBackend(Program.Options("persistent-read-failure") with
        {
            ObservationTimeout = TimeSpan.FromSeconds(1), MaximumRestarts = 1
        });
        await backend.StartAsync(default).ConfigureAwait(false);
        var snapshot = await HostingTests.WaitForSnapshotAsync(backend).ConfigureAwait(false);
        var epoch = backend.WorkerEpoch;
        var command = HostingTests.Command(snapshot, MediaOperation.Play);
        await backend.ExecuteAsync(HostingTests.Command(snapshot, MediaOperation.Stop), default).ConfigureAwait(false);
        await HostingTests.EventuallyAsync(async () =>
        {
            try
            {
                snapshot = await backend.ReadSnapshotAsync(default).ConfigureAwait(false);
                return backend.WorkerEpoch != epoch && snapshot.Sessions.Length == 1 &&
                       snapshot.Availability == MediaControlAvailability.Available;
            }
            catch (IOException) { return false; }
        }).ConfigureAwait(false);
        HostingTests.Check(backend.RestartCount == 1, "The poisoned worker did not use exactly one replacement.");
        HostingTests.Check(
            (await backend.ExecuteAsync(command, default).ConfigureAwait(false)).Status ==
            MediaBackendCommandStatus.SessionGone,
            "A pre-restart command reached the replacement.");
        HostingTests.Check(
            (await backend.ExecuteAsync(HostingTests.Command(snapshot, MediaOperation.Play), default)
                .ConfigureAwait(false)).Status == MediaBackendCommandStatus.Completed,
            "The replacement did not restore usable controls.");
    }

    private static async Task PersistentSnapshotBudgetAsync()
    {
        await using var backend = new OutOfProcessMediaBackend(Program.Options("recurring-read-failure") with
        {
            ObservationTimeout = TimeSpan.FromMilliseconds(400), MaximumRestarts = 1
        });
        var registry
            = new MediaBackendRegistry().Register(new MediaBackendRegistration("worker", "Worker", "", _ => backend,
                true));
        await using var composite = new CompositeMediaBackend(registry);
        await composite.StartAsync(default).ConfigureAwait(false);
        await HostingTests.EventuallyAsync(async () =>
        {
            await composite.ReadSnapshotAsync(default).ConfigureAwait(false);
            return composite.Backends[0].DiagnosticMessage
                ?.Contains("restart budget exhausted", StringComparison.Ordinal) == true;
        }).ConfigureAwait(false);
        HostingTests.Check(
            backend.WorkerProcessId == 0 && backend.RestartCount == 1 &&
            composite.Backends[0].Status == MediaBackendLifecycleStatus.Faulted,
            "Permanently failing observations escaped the restart budget.");
        HostingTests.Check(
            composite.Backends[0].DiagnosticMessage
                ?.Contains("Persistent snapshot failure.", StringComparison.Ordinal) == true,
            "The exhausted restart diagnostic lost the backend's read error.");
    }

    private static async Task SnapshotWindowResetAsync()
    {
        await using var backend = new OutOfProcessMediaBackend(Program.Options("short-read-failure") with
        {
            ObservationTimeout = TimeSpan.FromMilliseconds(900), MaximumRestarts = 0
        });
        await backend.StartAsync(default).ConfigureAwait(false);
        var snapshot = await HostingTests.WaitForSnapshotAsync(backend).ConfigureAwait(false);
        var epoch = backend.WorkerEpoch;
        for (var cycle = 0; cycle < 2; cycle++)
        {
            var revision = snapshot.Revision;
            await backend.ExecuteAsync(HostingTests.Command(snapshot, MediaOperation.Stop), default)
                .ConfigureAwait(false);
            await HostingTests.EventuallyAsync(async () =>
            {
                try
                {
                    snapshot = await backend.ReadSnapshotAsync(default).ConfigureAwait(false);
                    return snapshot.Revision > revision && snapshot.Sessions.Length == 1;
                }
                catch (IOException) { return false; }
            }).ConfigureAwait(false);
            await Task.Delay(250).ConfigureAwait(false);
        }

        HostingTests.Check(backend.WorkerEpoch == epoch && backend.RestartCount == 0 && backend.WorkerProcessId != 0,
            "A previous failure window retired a recovered worker.");
    }

    private static Task SnapshotHealthAsync()
    {
        var clock = new HealthClock();
        var failed = new WorkerHealth(clock);
        failed.Observe(true);
        clock.Advance(1);
        failed.Observe(false);
        clock.Advance(40);
        failed.Complete();
        HostingTests.Check(!failed.WasStable, "Time spent failing reset the restart budget.");
        var interrupted = new WorkerHealth(clock);
        interrupted.Observe(true);
        clock.Advance(29);
        interrupted.Observe(false);
        clock.Advance(20);
        interrupted.Observe(true);
        clock.Advance(29);
        interrupted.Complete();
        clock.Advance(50);
        interrupted.Observe(true);
        interrupted.Complete();
        HostingTests.Check(!interrupted.WasStable,
            "Separate healthy periods or cleanup time accumulated into stability.");
        var stable = new WorkerHealth(clock);
        stable.Observe(true);
        clock.Advance(30);
        stable.Observe(false);
        stable.Complete();
        HostingTests.Check(stable.WasStable, "A full healthy period did not replenish recovery.");
        return Task.CompletedTask;
    }

    internal static Task RunPartialRequestPeerAsync(string pipeName, Guid owner, int processId, string id) =>
        ReviewPartialRequestPeer.RunAsync(pipeName, processId, id);

    private sealed class HealthClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => this._ticks;
        public void Advance(int seconds) => this._ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
}