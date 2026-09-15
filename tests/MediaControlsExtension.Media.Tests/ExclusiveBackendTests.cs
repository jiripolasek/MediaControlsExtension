using System.Diagnostics;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class ExclusiveBackendTests
{
    [TestMethod]
    public void ConflictingSelectionsNeverInvokeFactories()
    {
        var calls = 0;
        var registry = new MediaBackendRegistry()
            .Register(Registration("a", () => { calls++; return Backend(); }, true))
            .Register(Registration("b", () => { calls++; return Backend(); }, true));
        Assert.ThrowsExactly<InvalidOperationException>(() => new CompositeMediaBackend(registry));
        Assert.ThrowsExactly<InvalidOperationException>(() => new CompositeMediaBackend(registry, ["a", "b"]));
        Assert.ThrowsExactly<ArgumentException>(() => registry.Register(Registration("c", Backend) with { ExclusiveGroup = " " }));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task FullSelectionRejectsConflictBeforeChangingDesiredState()
    {
        var registry = new MediaBackendRegistry().Register(Registration("a", Backend, true)).Register(Registration("b", Backend));
        await using var composite = new CompositeMediaBackend(registry);
        Assert.ThrowsExactly<InvalidOperationException>(() => composite.SetEnabledBackendsAsync(["a", "b"]));
        Assert.AreEqual("a", composite.Backends.Single(static state => state.IsEnabled).Id);
        await composite.SetEnabledBackendsAsync([]);
        await composite.StartAsync(default);
        Assert.IsTrue(composite.Backends.All(static state => !state.IsEnabled));
        Assert.IsEmpty((await composite.ReadSnapshotAsync(default)).Sessions);
    }

    [TestMethod]
    public async Task SwitchDrainsOldCommandsWhileIndependentProviderProgresses()
    {
        var first = Backend();
        first.BlockCommands();
        first.IgnoreCommandCancellation = true;
        var created = 0;
        var registry = new MediaBackendRegistry().Register(Registration("a", () => first, true))
            .Register(Registration("b", () => { created++; return Backend(); }))
            .Register(Registration("other", Backend) with { ExclusiveGroup = null });
        await using var composite = new CompositeMediaBackend(registry);
        await composite.StartAsync(default);
        await ReadyAsync(composite, "a");
        var session = (await composite.ReadSnapshotAsync(default)).Sessions.Single();
        var command = composite.ExecuteAsync(new(session.Id, session.BindingGeneration, MediaOperation.Play, []), default);
        await first.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using var cancellation = new CancellationTokenSource();
            var change = composite.SetEnabledAsync("b", true, cancellation.Token);
            await cancellation.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => change);
            Assert.AreEqual(0, created);
            Assert.IsFalse(composite.Backends.Single(static state => state.Id == "a").IsEnabled);
            Assert.IsTrue(composite.Backends.Single(static state => state.Id == "b").IsEnabled);
            Assert.AreEqual(MediaBackendLifecycleStatus.Starting, composite.Backends.Single(static state => state.Id == "b").Status);
            Assert.IsEmpty((await composite.ReadSnapshotAsync(default)).Sessions);
            await composite.SetEnabledAsync("other", true).WaitAsync(TimeSpan.FromSeconds(5));
            await ReadyAsync(composite, "other");
            first.ReleaseCommands();
            await command;
            await ReadyAsync(composite, "b");
            Assert.AreEqual(1, first.DisposeCount);
            Assert.AreEqual(1, created);
        }
        finally
        {
            first.ReleaseCommands();
        }
    }

    [TestMethod]
    public async Task FailedCleanupBlocksPeerFactoryAndRepeatedRequests()
    {
        var first = Backend();
        first.FailDisposal = true;
        var created = 0;
        var registry = new MediaBackendRegistry().Register(Registration("a", () => first, true))
            .Register(Registration("b", () => { created++; return Backend(); }));
        await using var composite = new CompositeMediaBackend(registry);
        await composite.StartAsync(default);
        await ReadyAsync(composite, "a");
        await composite.SetEnabledAsync("b", true);
        await composite.SetEnabledAsync("b", true);
        Assert.AreEqual(0, created);
        Assert.AreEqual(1, first.DisposeCount);
        var state = composite.Backends.Single(static state => state.Id == "b");
        Assert.AreEqual(MediaBackendLifecycleStatus.Faulted, state.Status);
        Assert.IsNotNull(state.DiagnosticMessage);
    }

    [TestMethod]
    public async Task BothSwitchDirectionsWaitForCleanupWithoutBlockingAnotherGroup()
    {
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Backend();
        first.DisposalBarrier = releaseFirst.Task;
        var second = Backend();
        second.DisposalBarrier = releaseSecond.Task;
        var originals = 0;
        var peers = 0;
        var registry = new MediaBackendRegistry().Register(Registration("a", () => ++originals == 1 ? first : Backend(), true))
            .Register(Registration("b", () => { peers++; return second; }))
            .Register(Registration("other", Backend) with { ExclusiveGroup = null });
        await using var composite = new CompositeMediaBackend(registry);
        try
        {
            await composite.StartAsync(default);
            await ReadyAsync(composite, "a");
            var toPeer = composite.SetEnabledAsync("b", true);
            await WaitForDisposalAsync(first);
            Assert.IsFalse(toPeer.IsCompleted);
            Assert.AreEqual(0, peers);
            await composite.SetEnabledAsync("other", true).WaitAsync(TimeSpan.FromSeconds(5));
            await ReadyAsync(composite, "other");
            releaseFirst.TrySetResult();
            await toPeer.WaitAsync(TimeSpan.FromSeconds(5));
            await ReadyAsync(composite, "b");
            var toOriginal = composite.SetEnabledAsync("a", true);
            await WaitForDisposalAsync(second);
            Assert.IsFalse(toOriginal.IsCompleted);
            Assert.AreEqual(1, originals);
            await composite.SetEnabledAsync("other", false).WaitAsync(TimeSpan.FromSeconds(5));
            releaseSecond.TrySetResult();
            await toOriginal.WaitAsync(TimeSpan.FromSeconds(5));
            await ReadyAsync(composite, "a");
            Assert.AreEqual(2, originals);
            Assert.AreEqual(1, peers);
            await composite.SetEnabledBackendsAsync([]).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(composite.Backends.All(static state => state.Status == MediaBackendLifecycleStatus.Disabled));
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseSecond.TrySetResult();
        }
    }

    [TestMethod]
    public async Task RapidSelectionSkipsSupersededPeerAndCreatesFreshOriginal()
    {
        var first = Backend();
        first.BlockCommands();
        first.IgnoreCommandCancellation = true;
        var originals = 0;
        var peers = 0;
        var registry = new MediaBackendRegistry().Register(Registration("a", () => ++originals == 1 ? first : Backend(), true))
            .Register(Registration("b", () => { peers++; return Backend(); }));
        await using var composite = new CompositeMediaBackend(registry);
        await composite.StartAsync(default);
        await ReadyAsync(composite, "a");
        var session = (await composite.ReadSnapshotAsync(default)).Sessions.Single();
        var command = composite.ExecuteAsync(new(session.Id, session.BindingGeneration, MediaOperation.Play, []), default);
        await first.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var peer = composite.SetEnabledAsync("b", true);
            var original = composite.SetEnabledBackendsAsync(["a"]);
            var repeated = composite.SetEnabledBackendsAsync(["a"]);
            Assert.IsFalse(repeated.IsCompleted);
            Assert.AreEqual(0, peers);
            first.ReleaseCommands();
            await Task.WhenAll(peer, original, repeated, command).WaitAsync(TimeSpan.FromSeconds(5));
            await ReadyAsync(composite, "a");
            Assert.AreEqual(2, originals);
            Assert.AreEqual(0, peers);
            Assert.AreEqual(MediaBackendLifecycleStatus.Disabled, composite.Backends.Single(static state => state.Id == "b").Status);
            Assert.AreNotEqual(session.Id, (await composite.ReadSnapshotAsync(default)).Sessions.Single().Id);
        }
        finally
        {
            first.ReleaseCommands();
        }
    }

    private static FakeMediaBackend Backend() => new(FakeMediaBackend.CreateSnapshot(1, "Player"));

    [TestMethod]
    public async Task FullSelectionWithRetryWaitsForAnUnchangedGroupsPendingTransition()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Backend();
        first.DisposalBarrier = release.Task;
        var registry = new MediaBackendRegistry()
            .Register(Registration("a", () => first, true))
            .Register(Registration("b", Backend))
            .Register(Registration("other", Backend, true) with { ExclusiveGroup = null });
        await using var composite = new CompositeMediaBackend(registry);
        try
        {
            await composite.StartAsync(default);
            await ReadyAsync(composite, "a");
            var switching = composite.SetEnabledAsync("b", true);
            await WaitForDisposalAsync(first);
            var selected = composite.SetEnabledBackendsAsync(["b", "other"], retryBackendId: "other");
            await Task.Delay(50);
            Assert.IsFalse(selected.IsCompleted);
            release.TrySetResult();
            await Task.WhenAll(switching, selected).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(MediaBackendLifecycleStatus.Disabled, composite.Backends.Single(static state => state.Id == "a").Status);
            Assert.AreEqual(MediaBackendLifecycleStatus.Ready, composite.Backends.Single(static state => state.Id == "b").Status);
        }
        finally { release.TrySetResult(); }
    }

    private static async Task WaitForDisposalAsync(FakeMediaBackend backend)
    {
        var timer = Stopwatch.StartNew();
        while (backend.DisposeCount == 0)
        {
            Assert.IsLessThan(TimeSpan.FromSeconds(5), timer.Elapsed);
            await Task.Delay(10);
        }
    }

    private static MediaBackendRegistration Registration(string id, Func<IMediaBackend> factory, bool enabled = false) =>
        new(id, id, string.Empty, _ => factory(), enabled) { ExclusiveGroup = "sessions" };

    private static async Task ReadyAsync(CompositeMediaBackend composite, string id)
    {
        var timer = Stopwatch.StartNew();
        while (composite.Backends.Single(state => state.Id == id).Status != MediaBackendLifecycleStatus.Ready)
        {
            Assert.IsLessThan(TimeSpan.FromSeconds(5), timer.Elapsed);
            await composite.ReadSnapshotAsync(default);
            await Task.Delay(10);
        }
    }
}