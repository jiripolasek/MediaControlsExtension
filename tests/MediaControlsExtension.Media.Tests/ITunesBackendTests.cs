// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.ITunes;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class ITunesBackendTests
{
    private static ITunesBackend CreateDisconnectedBackend() =>
        new(isProcessRunning: static _ => false);

    [TestMethod]
    public async Task InitialSnapshotWhenNotConnectedReturnsEmptySessionsAndDisconnected()
    {
        await using var backend = CreateDisconnectedBackend();
        await backend.StartAsync(default);

        var snapshot = await backend.ReadSnapshotAsync(default);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(MediaControlAvailability.Available, snapshot.Availability);
        Assert.AreEqual(MediaBackendConnectionState.Disconnected, snapshot.Connection);
        Assert.IsTrue(snapshot.Sessions.IsEmpty);
        Assert.IsTrue(snapshot.CurrentSessionHints.IsEmpty);
    }

    [TestMethod]
    public async Task ExecuteAsyncWithUnknownSessionReturnsSessionGone()
    {
        await using var backend = CreateDisconnectedBackend();
        await backend.StartAsync(default);

        var command = new MediaBackendCommand(
            new MediaBackendSessionId(999),
            BindingGeneration: 1,
            MediaOperation.Play,
            SessionsToPause: []);

        var result = await backend.ExecuteAsync(command, default);
        Assert.AreEqual(MediaBackendCommandStatus.SessionGone, result.Status);
    }

    [TestMethod]
    public async Task ExecuteAsyncWithInvalidBindingGenerationReturnsSessionGone()
    {
        await using var backend = CreateDisconnectedBackend();
        await backend.StartAsync(default);

        var command = new MediaBackendCommand(
            new MediaBackendSessionId(1),
            BindingGeneration: 999,
            MediaOperation.Play,
            SessionsToPause: []);

        var result = await backend.ExecuteAsync(command, default);
        Assert.AreEqual(MediaBackendCommandStatus.SessionGone, result.Status);
    }

    [TestMethod]
    public async Task ExecuteAsyncHonorsCancellationTokenBeforeExecution()
    {
        await using var backend = CreateDisconnectedBackend();
        await backend.StartAsync(default);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var command = new MediaBackendCommand(
            new MediaBackendSessionId(1),
            BindingGeneration: 1,
            MediaOperation.Play,
            SessionsToPause: []);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await backend.ExecuteAsync(command, cts.Token));
    }

    [TestMethod]
    public async Task GetArtworkAsyncWithMismatchedKeyReturnsNull()
    {
        await using var backend = CreateDisconnectedBackend();
        await backend.StartAsync(default);

        var key = new MediaArtworkKey(new MediaSessionId(1), Version: 999);
        var artwork = await backend.GetArtworkAsync(key, default);
        Assert.IsNull(artwork);
    }

    [TestMethod]
    public async Task StartAsyncThrowsWhenAlreadyStarted()
    {
        await using var backend = CreateDisconnectedBackend();
        await backend.StartAsync(default);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await backend.StartAsync(default));
    }

    [TestMethod]
    public async Task OperationsThrowWhenDisposed()
    {
        var backend = CreateDisconnectedBackend();
        await backend.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            async () => await backend.StartAsync(default));

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            async () => await backend.ReadSnapshotAsync(default));
    }

    [TestMethod]
    public async Task CanceledStartDoesNotConsumeBackend()
    {
        await using var backend = CreateDisconnectedBackend();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await backend.StartAsync(cts.Token));

        await backend.StartAsync(default);
    }

    [TestMethod]
    public void ITunesEventSinkDetachReleasesDelegateAndSuppressesCallbacks()
    {
        var eventCount = 0;
        var sink = new JPSoftworks.MediaControlsExtension.Media.ITunes.Interop.ITunesEventSink(_ => eventCount++);
        Assert.IsFalse(sink.IsDetached);

        // Before detachment, native COM invocation succeeds and increments the counter
        var hrBefore = JPSoftworks.MediaControlsExtension.Media.ITunes.Interop.ITunesNative.DispatchInvoke(sink.IUnknownPointer, 1);
        Assert.AreEqual(0, hrBefore);
        Assert.AreEqual(1, eventCount);

        // Detach releases the delegate callback reference and flags the sink as detached
        sink.Detach();
        Assert.IsTrue(sink.IsDetached);

        // Subsequent native COM invocations are safely ignored and do not invoke the callback
        var hrAfter = JPSoftworks.MediaControlsExtension.Media.ITunes.Interop.ITunesNative.DispatchInvoke(sink.IUnknownPointer, 1);
        Assert.AreEqual(0, hrAfter);
        Assert.AreEqual(1, eventCount);

        sink.Dispose();
    }
}
