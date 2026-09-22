// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.ITunes;
using JPSoftworks.MediaControlsExtension.Media.State;
using JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class ITunesPlaybackTests
{
    [TestMethod]
    public async Task ConfirmedPauseClearsPredictionBeforeExternalResume()
    {
        await using var fixture = await ITunesNativeFixture.CreateAsync();
        await fixture.RefreshAsync();
        var store = new MediaStateStore();
        store.ApplyBackendSnapshot(await fixture.Backend.ReadSnapshotAsync(default));
        var command = AcceptCommand(store, MediaOperation.Pause);
        Assert.IsTrue(store.Current.Sessions.Single().PlaybackInfo.IsOptimistic);

        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await fixture.ExecuteAsync(MediaOperation.Pause)).Status);
        store.CompleteCommand(command, new(1), succeeded: true);
        var paused = store.ApplyBackendSnapshot(await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single().PlaybackInfo;
        Assert.AreEqual(MediaPlaybackState.Paused, paused.ConfirmedState);
        Assert.IsFalse(paused.IsOptimistic);
        Assert.AreEqual(MediaOperation.Play, paused.PrimaryOperation);

        fixture.PlayerState = ITPlayerState.Playing;
        await fixture.SendPlaybackEventAsync(ITunesEventDispId.PlayerPlay);
        var resumed = store.ApplyBackendSnapshot(await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single().PlaybackInfo;
        Assert.AreEqual(MediaPlaybackState.Playing, resumed.ConfirmedState);
        Assert.AreEqual(MediaPlaybackState.Playing, resumed.EffectiveState);
        Assert.IsFalse(resumed.IsOptimistic);
        Assert.AreEqual(MediaOperation.Pause, resumed.PrimaryOperation);
    }

    [TestMethod]
    public async Task PauseIsNotConfirmedUntilTheNativeStateChanges()
    {
        await using var fixture = await ITunesNativeFixture.CreateAsync();
        await fixture.RefreshAsync();
        var store = new MediaStateStore();
        store.ApplyBackendSnapshot(await fixture.Backend.ReadSnapshotAsync(default));
        var command = AcceptCommand(store, MediaOperation.Pause);
        fixture.ApplyPlaybackCommands = false;

        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await fixture.ExecuteAsync(MediaOperation.Pause)).Status);
        store.CompleteCommand(command, new(1), succeeded: true);
        var pending = store.ApplyBackendSnapshot(await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single().PlaybackInfo;
        Assert.AreEqual(MediaPlaybackState.Playing, pending.ConfirmedState);
        Assert.IsTrue(pending.IsOptimistic);

        fixture.PlayerState = ITPlayerState.Stopped;
        await fixture.SendPlaybackEventAsync(ITunesEventDispId.PlayerStop);
        var confirmed = store.ApplyBackendSnapshot(await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single().PlaybackInfo;
        Assert.AreEqual(MediaPlaybackState.Paused, confirmed.ConfirmedState);
        Assert.IsFalse(confirmed.IsOptimistic);
    }

    [TestMethod]
    public async Task ExplicitStopRemovesTheSessionAndClearsItsPrediction()
    {
        await using var fixture = await ITunesNativeFixture.CreateAsync();
        await fixture.RefreshAsync();
        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await fixture.ExecuteAsync(MediaOperation.Pause)).Status);
        await fixture.RefreshAsync();
        var store = new MediaStateStore();
        store.ApplyBackendSnapshot(await fixture.Backend.ReadSnapshotAsync(default));
        Assert.AreEqual(MediaPlaybackState.Paused, store.Current.Sessions.Single().PlaybackInfo.ConfirmedState);
        var command = AcceptCommand(store, MediaOperation.Stop);

        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await fixture.ExecuteAsync(MediaOperation.Stop)).Status);
        store.CompleteCommand(command, new(1), succeeded: true);
        await fixture.RefreshAsync();
        var stopped = store.ApplyBackendSnapshot(await fixture.Backend.ReadSnapshotAsync(default));
        Assert.IsEmpty(stopped.Sessions);
        Assert.IsNull(stopped.CurrentSessionId);
        Assert.IsFalse(store.TryExpirePrediction(command.SessionId, new(1), out _));
    }

    [TestMethod]
    [DataRow(MediaOperation.Pause, MediaOperation.Stop, MediaPlaybackState.Paused)]
    [DataRow(MediaOperation.Play, MediaOperation.Pause, MediaPlaybackState.Playing)]
    public async Task FailedCommandDoesNotOverrideObservedPlayback(
        MediaOperation first, MediaOperation failing, MediaPlaybackState expected)
    {
        await using var fixture = await ITunesNativeFixture.CreateAsync();
        await fixture.RefreshAsync();
        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await fixture.ExecuteAsync(first)).Status);
        fixture.CommandResult = unchecked((int)0x80004005);

        Assert.AreEqual(MediaBackendCommandStatus.Failed, (await fixture.ExecuteAsync(failing)).Status);
        await fixture.RefreshAsync();
        Assert.AreEqual(expected, (await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single().PlaybackState);
    }

    [TestMethod]
    public async Task ConnectingToAnAlreadyPausedTrackReportsPaused()
    {
        await using var fixture = await ITunesNativeFixture.CreateAsync();
        fixture.PlayerState = ITPlayerState.Stopped;
        await fixture.RefreshAsync();

        Assert.AreEqual(MediaPlaybackState.Paused, (await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single().PlaybackState);
    }

    [TestMethod]
    public async Task MissingTrackRemovesTheSessionAndItsPausePrediction()
    {
        await using var fixture = await ITunesNativeFixture.CreateAsync();
        await fixture.RefreshAsync();
        var store = new MediaStateStore();
        store.ApplyBackendSnapshot(await fixture.Backend.ReadSnapshotAsync(default));
        AcceptCommand(store, MediaOperation.Pause);
        fixture.HasCurrentTrack = false;

        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await fixture.ExecuteAsync(MediaOperation.Pause)).Status);
        Assert.IsEmpty(store.ApplyBackendSnapshot(await fixture.Backend.ReadSnapshotAsync(default)).Sessions);

        fixture.HasCurrentTrack = true;
        await fixture.RefreshAsync();
        var restored = store.ApplyBackendSnapshot(await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single().PlaybackInfo;
        Assert.AreEqual(MediaPlaybackState.Paused, restored.ConfirmedState);
        Assert.IsFalse(restored.IsOptimistic);
    }

    [TestMethod]
    public async Task NoCurrentTrackDoesNotCreateAPausedSession()
    {
        await using var fixture = await ITunesNativeFixture.CreateAsync();
        fixture.PlayerState = ITPlayerState.Stopped;
        fixture.HasCurrentTrack = false;
        await fixture.RefreshAsync();

        Assert.IsEmpty((await fixture.Backend.ReadSnapshotAsync(default)).Sessions);
    }

    [TestMethod]
    public async Task ExternalPauseIsReportedWithoutAnExtensionCommand()
    {
        await using var fixture = await ITunesNativeFixture.CreateAsync();
        await fixture.RefreshAsync();
        fixture.PlayerState = ITPlayerState.Stopped;
        await fixture.SendPlaybackEventAsync(ITunesEventDispId.PlayerStop);

        Assert.AreEqual(MediaPlaybackState.Paused, (await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single().PlaybackState);
    }

    private static ResolvedMediaCommand AcceptCommand(MediaStateStore store, MediaOperation operation)
    {
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted,
            store.TryResolveCommand(new(MediaCommandTarget.CurrentSession, operation), out var command));
        store.ApplyAcceptedCommand(command, new(1));
        return command;
    }
}