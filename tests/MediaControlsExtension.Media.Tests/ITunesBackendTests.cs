// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Diagnostics;
using JPSoftworks.MediaControlsExtension.Helpers;
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

    [TestMethod]
    [DataRow((int)ITunesEventDispId.DatabaseChanged)]
    [DataRow((int)ITunesEventDispId.PlayerPlay)]
    [DataRow((int)ITunesEventDispId.PlayerStop)]
    [DataRow((int)ITunesEventDispId.PlayerPlayingTrackChanged)]
    [DataRow((int)ITunesEventDispId.PlayerPlayingTrackInfoChanged)]
    [DataRow((int)ITunesEventDispId.ComCallsEnabled)]
    public void PresentationEventsRequestRefresh(int dispId)
    {
        Assert.AreEqual(ITunesEventAction.Refresh, ITunesEventClassifier.Classify(dispId));
    }

    [TestMethod]
    [DataRow((int)ITunesEventDispId.AboutToPromptUserToQuit)]
    [DataRow((int)ITunesEventDispId.Quitting)]
    public void QuitEventsRequestDisconnectInsteadOfRefresh(int dispId)
    {
        Assert.AreEqual(ITunesEventAction.Disconnect, ITunesEventClassifier.Classify(dispId));

        var scheduler = new ITunesRefreshScheduler();
        Assert.IsFalse(scheduler.HasPendingRefresh);
    }

    [TestMethod]
    public void IrrelevantITunesEventsAreIgnored()
    {
        Assert.AreEqual(ITunesEventAction.Ignore, ITunesEventClassifier.Classify((int)ITunesEventDispId.SoundVolumeChanged));
        Assert.AreEqual(ITunesEventAction.Ignore, ITunesEventClassifier.Classify(999));
    }

    [TestMethod]
    public void RefreshSchedulerCoalescesQueuedAndInFlightEventsWithoutLosingFollowUp()
    {
        var scheduler = new ITunesRefreshScheduler();

        Assert.IsTrue(scheduler.RequestRefresh());
        Assert.IsFalse(scheduler.RequestRefresh());
        Assert.IsTrue(scheduler.TryBeginRefresh());
        Assert.IsFalse(scheduler.RequestRefresh());
        Assert.IsTrue(scheduler.CompleteRefresh());
        Assert.IsTrue(scheduler.TryBeginRefresh());
        Assert.IsFalse(scheduler.CompleteRefresh());
        Assert.IsFalse(scheduler.HasPendingRefresh);
    }

    [TestMethod]
    public void ClearingRefreshSchedulerDropsQueuedAndFollowUpWork()
    {
        var scheduler = new ITunesRefreshScheduler();

        Assert.IsTrue(scheduler.RequestRefresh());
        scheduler.Clear();
        Assert.IsFalse(scheduler.TryBeginRefresh());

        Assert.IsTrue(scheduler.RequestRefresh());
        Assert.IsTrue(scheduler.TryBeginRefresh());
        Assert.IsFalse(scheduler.RequestRefresh());
        scheduler.Clear();
        Assert.IsFalse(scheduler.CompleteRefresh());
        Assert.IsFalse(scheduler.HasPendingRefresh);
    }

    [TestMethod]
    public void ConnectedITunesWithoutCurrentTrackHasNoSession()
    {
        var snapshot = ITunesBackend.CreateSnapshot(
            isConnected: true,
            revision: 4,
            bindingGeneration: 8,
            mediaProperties: null,
            timeline: MediaTimelinePropertiesSnapshot.Empty,
            playbackState: MediaPlaybackState.Playing,
            capabilities: MediaCapabilities.Play);

        Assert.AreEqual(MediaBackendConnectionState.Connected, snapshot.Connection);
        Assert.IsTrue(snapshot.Sessions.IsEmpty);
        Assert.IsTrue(snapshot.CurrentSessionHints.IsEmpty);
    }

    [TestMethod]
    [DataRow("Apple.iTunes")]
    [DataRow("iTunes.exe")]
    [DataRow("AppleInc.iTunes_nzyj5cx40ttqa!iTunes")]
    public void ITunesClaimsEachInstallationIdentityForBothGsmtcHosts(string applicationId)
    {
        Assert.IsTrue(ITunesSourceClaims.ReplacesGsmtcSources.Contains(new MediaBackendSourceClaim("gsmtc", applicationId)));
        Assert.IsTrue(ITunesSourceClaims.ReplacesGsmtcSources.Contains(new MediaBackendSourceClaim("gsmtc.worker", applicationId)));
    }

    [TestMethod]
    [DataRow("C:\\Program Files\\iTunes\\iTunes.exe", "Apple.iTunes")]
    [DataRow("C:\\Program Files\\WindowsApps\\AppleInc.iTunes_12.13.8.3_x64__nzyj5cx40ttqa\\iTunes.exe", "AppleInc.iTunes_nzyj5cx40ttqa!iTunes")]
    public void NativeIdentityMatchesTheDiscoveredITunesInstallation(string executablePath, string expectedApplicationId)
    {
        var identity = ITunesBackend.CreateNativeApplicationIdentity(executablePath);

        Assert.AreEqual(expectedApplicationId, identity.ApplicationId);
        Assert.AreEqual(executablePath, identity.ExecutablePath);
    }

    [TestMethod]
    public void ITunesClaimsBothMediaControllerHelperIdentitiesForBothGsmtcHosts()
    {
        string[] helperIds =
        [
            "49586DaveAntoine.MediaControllerforiTunes_9bzempp7dntjg",
            "49586DaveAntoine.MediaControllerforiTunes_9bzempp7dntjg!App",
        ];

        foreach (var helperId in helperIds)
        {
            Assert.IsTrue(ITunesSourceClaims.ReplacesGsmtcSources.Contains(new MediaBackendSourceClaim("gsmtc", helperId)));
            Assert.IsTrue(ITunesSourceClaims.ReplacesGsmtcSources.Contains(new MediaBackendSourceClaim("gsmtc.worker", helperId)));
        }
    }

    [TestMethod]
    public void ProcessExitSubscriptionDetachesAndDisposesItsOwnedProcess()
    {
        var process = Process.GetCurrentProcess();
        var subscription = new ITunesProcessExitSubscription();
        subscription.Attach(process, _ => Assert.Fail("The current test process must not exit."));

        Assert.IsTrue(subscription.IsAttached);
        Assert.AreNotEqual(0, subscription.ProcessId);

        subscription.Dispose();

        Assert.IsFalse(subscription.IsAttached);
        Assert.AreEqual(0, subscription.ProcessId);
    }
}
