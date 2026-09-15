using System.Collections.Concurrent;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting.Tests;

internal static class ReviewRegressionTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string directory) =>
    [
        ("Startup and a changing policy use the startup budget", StartupAsync),
        ("The worker honors the configured request deadline", RequestDeadlineAsync),
        ("Exhausted recovery becomes Faulted and can be explicitly retried", TerminalFaultAsync),
        ("Policy changes preserve unaffected routes and retire excluded identities", PolicyIdentityAsync),
        ("Canceled request bursts retain admission until worker completion", CancellationBurstAsync),
        ("Artwork completion bursts keep the worker connected without extra snapshots", ArtworkBurstAsync),
        ("Late activation frames cannot revive a canceled command or disconnect its peers", LateActivationAsync),
        ("An unusable worker log directory does not prevent media startup", () => LoggingFailureAsync(directory)),
        ("Routine hosted observations preserve composite topology credits", ObservationSignalsAsync),
    ];

    private static async Task StartupAsync()
    {
        await using var backend = new OutOfProcessMediaBackend(Program.Options("slow-start") with
        {
            StartupTimeout = TimeSpan.FromSeconds(3), RequestTimeout = TimeSpan.FromMilliseconds(150), MaximumRestarts = 0,
        });
        await backend.StartAsync(default).ConfigureAwait(false);
        await HostingTests.EventuallyAsync(() => backend.WorkerEpoch != Guid.Empty).ConfigureAwait(false);
        await backend.ApplySourcePolicyAsync(new(1, ["other.player"]), default).ConfigureAwait(false);
        var snapshot = await HostingTests.WaitForSnapshotAsync(backend).ConfigureAwait(false);
        HostingTests.Check(snapshot.SourcePolicyRevision == 1 && snapshot.Sessions.Length == 1 && backend.RestartCount == 0,
            "A short request deadline interrupted backend startup or lost its changed policy.");
    }

    private static async Task RequestDeadlineAsync()
    {
        await using var backend = new OutOfProcessMediaBackend(Program.Options("slow-command") with { RequestTimeout = TimeSpan.FromSeconds(8) });
        await backend.StartAsync(default).ConfigureAwait(false);
        var snapshot = await HostingTests.WaitForSnapshotAsync(backend).ConfigureAwait(false);
        var epoch = backend.WorkerEpoch;
        var result = await backend.ExecuteAsync(HostingTests.Command(snapshot, MediaOperation.Stop), default).ConfigureAwait(false);
        HostingTests.Check(result.Status == MediaBackendCommandStatus.Completed && backend.WorkerEpoch == epoch,
            "The worker imposed a shorter request deadline than its owner configured.");
    }

    private static async Task TerminalFaultAsync()
    {
        var created = 0;
        await using var owner = new MediaWorkerOwner();
        var registry = new MediaBackendRegistry().Register(new("worker", "Worker", "", _ =>
            new OutOfProcessMediaBackend(Program.Options(++created == 1 ? "hang-start" : "synthetic") with
            {
                StartupTimeout = created == 1 ? TimeSpan.FromMilliseconds(400) : TimeSpan.FromSeconds(3),
                ShutdownTimeout = TimeSpan.FromMilliseconds(150), MaximumRestarts = 0,
            }, owner), true));
        await using var composite = new CompositeMediaBackend(registry);
        await composite.StartAsync(default).ConfigureAwait(false);
        await HostingTests.EventuallyAsync(async () =>
        {
            await composite.ReadSnapshotAsync(default).ConfigureAwait(false);
            return composite.Backends.Single().Status == MediaBackendLifecycleStatus.Faulted;
        }).ConfigureAwait(false);
        await composite.SetEnabledAsync("worker", true).ConfigureAwait(false);
        await HostingTests.EventuallyAsync(async () => (await composite.ReadSnapshotAsync(default).ConfigureAwait(false)).Sessions.Length == 1).ConfigureAwait(false);
        HostingTests.Check(created == 2 && composite.Backends.Single().Status == MediaBackendLifecycleStatus.Ready,
            "An explicit retry could not replace the exhausted worker provider.");
    }

    private static async Task PolicyIdentityAsync()
    {
        await using var backend = new OutOfProcessMediaBackend(Program.Options("slow-policy"));
        await backend.StartAsync(default).ConfigureAwait(false);
        var original = await HostingTests.WaitForSnapshotAsync(backend).ConfigureAwait(false);
        var update = backend.ApplySourcePolicyAsync(new(1, ["other.player"]), default);
        var during = await backend.ReadSnapshotAsync(default).ConfigureAwait(false);
        HostingTests.Check(during.Sessions.Length == 1 && during.Sessions[0].Id == original.Sessions[0].Id &&
            during.Availability == MediaControlAvailability.Available, "An unrelated policy change withdrew a healthy route.");
        await update.ConfigureAwait(false);
        HostingTests.Check((await backend.ExecuteAsync(HostingTests.Command(original, MediaOperation.Play), default).ConfigureAwait(false)).Status == MediaBackendCommandStatus.Completed,
            "An unrelated policy change broke a captured command target.");
        var exclude = backend.ApplySourcePolicyAsync(new(2, ["spike.player"]), default);
        HostingTests.Check((await backend.ReadSnapshotAsync(default).ConfigureAwait(false)).Sessions.IsEmpty,
            "Exclusion did not retire the route immediately.");
        await exclude.ConfigureAwait(false);
        await backend.ApplySourcePolicyAsync(new(3, []), default).ConfigureAwait(false);
        await HostingTests.EventuallyAsync(async () => (await backend.ReadSnapshotAsync(default).ConfigureAwait(false)).Sessions.Length == 1).ConfigureAwait(false);
        HostingTests.Check((await backend.ReadSnapshotAsync(default).ConfigureAwait(false)).Sessions[0].Id != original.Sessions[0].Id,
            "Re-inclusion reused an excluded session identity.");
    }

    private static async Task CancellationBurstAsync()
    {
        await using var backend = new OutOfProcessMediaBackend(Program.Options("slow-cancel"));
        await backend.StartAsync(default).ConfigureAwait(false);
        var snapshot = await HostingTests.WaitForSnapshotAsync(backend).ConfigureAwait(false);
        var epoch = backend.WorkerEpoch;
        for (var batch = 1; batch <= 3; batch++)
        {
            var cancellations = Enumerable.Range(0, 32).Select(static _ => new CancellationTokenSource()).ToArray();
            try
            {
                var tasks = cancellations.Select(cancel => backend.ExecuteAsync(HostingTests.Command(snapshot, MediaOperation.Stop), cancel.Token)).ToArray();
                await HostingTests.EventuallyAsync(async () => (await backend.ReadSnapshotAsync(default).ConfigureAwait(false)).Sessions.FirstOrDefault()?
                    .MediaProperties.Subtitle.Contains($"Commands: {batch * 32};", StringComparison.Ordinal) == true).ConfigureAwait(false);
                foreach (var cancellation in cancellations) { await cancellation.CancelAsync().ConfigureAwait(false); }
                foreach (var task in tasks)
                {
                    try { await task.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
            }
            finally { foreach (var cancellation in cancellations) { cancellation.Dispose(); } }
        }

        HostingTests.Check((await backend.ExecuteAsync(HostingTests.Command(snapshot, MediaOperation.Play), default).ConfigureAwait(false)).Status == MediaBackendCommandStatus.Completed &&
            backend.WorkerEpoch == epoch && backend.RestartCount == 0, "Cancellation released admission before worker completion.");
    }

    private static async Task ArtworkBurstAsync()
    {
        await using var backend = new OutOfProcessMediaBackend(Program.Options("quiet-artwork"));
        await backend.StartAsync(default).ConfigureAwait(false);
        await HostingTests.WaitForSnapshotAsync(backend).ConfigureAwait(false);
        await Task.Delay(300).ConfigureAwait(false);
        var snapshot = await backend.ReadSnapshotAsync(default).ConfigureAwait(false);
        var epoch = backend.WorkerEpoch;
        for (var batch = 0; batch < 8; batch++)
        {
            var images = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
                backend.GetArtworkAsync(snapshot.Sessions[0].MediaProperties.Artwork!.Value, default).AsTask())).ConfigureAwait(false);
            HostingTests.Check(images.All(static image => image is not null), "An artwork completion burst lost the worker.");
        }
        await Task.Delay(300).ConfigureAwait(false);
        HostingTests.Check(backend.WorkerEpoch == epoch && (await backend.ReadSnapshotAsync(default).ConfigureAwait(false)).Revision == snapshot.Revision,
            "Artwork requests caused redundant snapshots or retired the worker.");
    }

    private static async Task LateActivationAsync()
    {
        var activations = 0;
        await using var backend = new OutOfProcessMediaBackend(Program.Options("review-late-activation") with
        {
            ActivateSource = (_, _) => { Interlocked.Increment(ref activations); return Task.FromResult(true); },
        });
        await backend.StartAsync(default).ConfigureAwait(false);
        var snapshot = await HostingTests.WaitForSnapshotAsync(backend).ConfigureAwait(false);
        var epoch = backend.WorkerEpoch;
        using var cancel = new CancellationTokenSource();
        var pending = backend.ExecuteAsync(HostingTests.Command(snapshot, MediaOperation.ActivateSource), cancel.Token);
        await HostingTests.EventuallyAsync(async () => (await backend.ReadSnapshotAsync(default).ConfigureAwait(false)).Sessions[0].MediaProperties.Title == "Admitted").ConfigureAwait(false);
        await cancel.CancelAsync().ConfigureAwait(false);
        try { await pending.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        var result = await backend.ExecuteAsync(HostingTests.Command(snapshot, MediaOperation.Play), default).ConfigureAwait(false);
        HostingTests.Check(result.Status == MediaBackendCommandStatus.Completed && backend.WorkerEpoch == epoch && activations == 0,
            "Late activation invoked its owner or retired the connection.");
    }

    private static async Task LoggingFailureAsync(string directory)
    {
        var path = Path.Combine(directory, $"blocked-log-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, "This file prevents directory creation.").ConfigureAwait(false);
        await using var backend = new OutOfProcessMediaBackend(Program.Options("application-plain") with
        {
            Logging = new(path, false), MaximumRestarts = 0,
        });
        await backend.StartAsync(default).ConfigureAwait(false);
        var snapshot = await HostingTests.WaitForSnapshotAsync(backend).ConfigureAwait(false);
        HostingTests.Check((await backend.ExecuteAsync(HostingTests.Command(snapshot, MediaOperation.Play), default).ConfigureAwait(false)).Status == MediaBackendCommandStatus.Completed,
            "Log-file setup disabled a healthy backend.");
    }

    private static async Task ObservationSignalsAsync()
    {
        var signals = new ConcurrentQueue<MediaBackendSignal>();
        await using var owner = new MediaWorkerOwner();
        var registry = new MediaBackendRegistry().Register(new("worker", "Worker", "", _ =>
            new OutOfProcessMediaBackend(Program.Options("synthetic"), owner), true));
        await using var composite = new CompositeMediaBackend(registry);
        await composite.StartAsync(default).ConfigureAwait(false);
        using var cancel = new CancellationTokenSource();
        var watch = Task.Run(async () =>
        {
            try
            {
                await foreach (var signal in composite.WatchAsync(cancel.Token).ConfigureAwait(false))
                {
                    signals.Enqueue(signal);
                    await composite.ReadSnapshotAsync(cancel.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        });
        try
        {
            await HostingTests.EventuallyAsync(async () => (await composite.ReadSnapshotAsync(default).ConfigureAwait(false)).Sessions.Length == 1).ConfigureAwait(false);
            await Task.Delay(300).ConfigureAwait(false);
            while (signals.TryDequeue(out _)) { }
            var before = await composite.ReadSnapshotAsync(default).ConfigureAwait(false);
            await composite.ExecuteAsync(HostingTests.Command(before, MediaOperation.Play), default).ConfigureAwait(false);
            await HostingTests.EventuallyAsync(async () => (await composite.ReadSnapshotAsync(default).ConfigureAwait(false)).Sessions[0].PlaybackState == MediaPlaybackState.Playing).ConfigureAwait(false);
            HostingTests.Check(!signals.IsEmpty && signals.All(static signal => signal == MediaBackendSignal.ObservationsChanged),
                "An ordinary playback observation consumed topology credits.");
        }
        finally { await cancel.CancelAsync().ConfigureAwait(false); await watch.ConfigureAwait(false); }
    }
}