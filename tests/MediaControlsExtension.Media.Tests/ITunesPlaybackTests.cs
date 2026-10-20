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
    [DataRow(MediaOperation.Play)]
    [DataRow(MediaOperation.Pause)]
    public async Task CompletedPlaybackKeepsIntentUntilItsFreshObservationArrives(MediaOperation operation)
    {
        await using var fixture = await ITunesNativeFixture.CreateAsync();
        var desired = operation == MediaOperation.Play ? MediaPlaybackState.Playing : MediaPlaybackState.Paused;
        var oppositeState = operation == MediaOperation.Play ? MediaPlaybackState.Paused : MediaPlaybackState.Playing;
        var inverse = operation == MediaOperation.Play ? MediaOperation.Pause : MediaOperation.Play;
        fixture.PlayerState = operation == MediaOperation.Play ? ITPlayerState.Playing : ITPlayerState.Stopped;
        await fixture.RefreshAsync();
        var store = new MediaStateStore();
        store.ApplyBackendSnapshot(await fixture.Backend.ReadSnapshotAsync(default));
        var preceding = AcceptCommand(store, inverse);
        var precedingResult = await fixture.ExecuteAsync(inverse);
        Assert.AreEqual(MediaBackendCommandStatus.Completed, precedingResult.Status);
        var delayed = await fixture.Backend.ReadSnapshotAsync(default);
        store.CompleteCommand(preceding, new(1), succeeded: true, precedingResult.ConfirmedPlaybackRevision);
        var latest = AcceptCommand(store, operation, 2);

        var result = await fixture.ExecuteAsync(operation);
        Assert.AreEqual(MediaBackendCommandStatus.Completed, result.Status);
        var completed = store.CompleteCommand(latest, new(2), succeeded: true, result.ConfirmedPlaybackRevision);

        Assert.IsTrue(completed.Sessions.Single().PlaybackInfo.IsOptimistic);
        var waiting = store.ApplyBackendSnapshot(delayed).Sessions.Single().PlaybackInfo;
        Assert.AreEqual(desired, waiting.EffectiveState);
        Assert.AreEqual(inverse, waiting.PrimaryOperation);
        Assert.IsTrue(waiting.IsOptimistic);
        var confirmed = store.ApplyBackendSnapshot(await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single().PlaybackInfo;
        Assert.AreEqual(desired, confirmed.EffectiveState);
        Assert.IsFalse(confirmed.IsOptimistic);

        fixture.PlayerState = operation == MediaOperation.Play ? ITPlayerState.Stopped : ITPlayerState.Playing;
        await fixture.SendPlaybackEventAsync(
            operation == MediaOperation.Play ? ITunesEventDispId.PlayerStop : ITunesEventDispId.PlayerPlay);
        var external = store.ApplyBackendSnapshot(await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single().PlaybackInfo;
        Assert.AreEqual(oppositeState, external.EffectiveState);
        Assert.AreEqual(operation, external.PrimaryOperation);
        Assert.IsFalse(external.IsOptimistic);
    }

    [TestMethod]
    [DataRow(MediaOperation.Play)]
    [DataRow(MediaOperation.Pause)]
    public async Task UnchangedPlaybackCommandPublishesAFreshObservation(MediaOperation operation)
    {
        await using var fixture = await ITunesNativeFixture.CreateAsync();
        fixture.PlayerState = operation == MediaOperation.Play ? ITPlayerState.Playing : ITPlayerState.Stopped;
        await fixture.RefreshAsync();
        var before = await fixture.Backend.ReadSnapshotAsync(default);
        await using var changes = fixture.Backend.WatchAsync(default).GetAsyncEnumerator();
        Assert.IsTrue(await changes.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));

        var result = await fixture.ExecuteAsync(operation);
        var after = await fixture.Backend.ReadSnapshotAsync(default);

        Assert.AreEqual(MediaBackendCommandStatus.Completed, result.Status);
        Assert.IsGreaterThan(before.Sessions[0].PlaybackObservationRevision, result.ConfirmedPlaybackRevision);
        Assert.IsGreaterThan(before.Revision, after.Revision);
        Assert.IsTrue(await changes.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsGreaterThanOrEqualTo(result.ConfirmedPlaybackRevision, after.Sessions[0].PlaybackObservationRevision);
        Assert.AreEqual(before.Sessions[0].PlaybackState, after.Sessions[0].PlaybackState);
    }

    [TestMethod]
    public async Task ConfirmedPauseClearsPredictionBeforeExternalResume()
    {
        await using var fixture = await ITunesNativeFixture.CreateAsync();
        await fixture.RefreshAsync();
        var store = new MediaStateStore();
        store.ApplyBackendSnapshot(await fixture.Backend.ReadSnapshotAsync(default));
        var command = AcceptCommand(store, MediaOperation.Pause);
        Assert.IsTrue(store.Current.Sessions.Single().PlaybackInfo.IsOptimistic);

        var result = await fixture.ExecuteAsync(MediaOperation.Pause);
        Assert.AreEqual(MediaBackendCommandStatus.Completed, result.Status);
        store.CompleteCommand(command, new(1), succeeded: true, result.ConfirmedPlaybackRevision);
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
    [DataRow(MediaOperation.Play)]
    [DataRow(MediaOperation.Pause)]
    public async Task SuccessfulPlaybackWaitsForTheNativeStateChange(MediaOperation operation)
    {
        await using var fixture = await ITunesNativeFixture.CreateAsync();
        fixture.PlayerState = operation == MediaOperation.Play ? ITPlayerState.Stopped : ITPlayerState.Playing;
        await fixture.RefreshAsync();
        var store = new MediaStateStore();
        store.ApplyBackendSnapshot(await fixture.Backend.ReadSnapshotAsync(default));
        var command = AcceptCommand(store, operation);
        fixture.ApplyPlaybackCommands = false;

        var result = await fixture.ExecuteAsync(operation);
        Assert.AreEqual(MediaBackendCommandStatus.Completed, result.Status);
        store.CompleteCommand(command, new(1), succeeded: true, result.ConfirmedPlaybackRevision);
        var pending = store.ApplyBackendSnapshot(await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single().PlaybackInfo;
        var desired = operation == MediaOperation.Play ? MediaPlaybackState.Playing : MediaPlaybackState.Paused;
        Assert.AreEqual(operation == MediaOperation.Play ? MediaPlaybackState.Paused : MediaPlaybackState.Playing, pending.ConfirmedState);
        Assert.AreEqual(desired, pending.EffectiveState);
        Assert.IsTrue(pending.IsOptimistic);

        fixture.PlayerState = operation == MediaOperation.Play ? ITPlayerState.Playing : ITPlayerState.Stopped;
        await fixture.SendPlaybackEventAsync(
            operation == MediaOperation.Play ? ITunesEventDispId.PlayerPlay : ITunesEventDispId.PlayerStop);
        var confirmed = store.ApplyBackendSnapshot(await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single().PlaybackInfo;
        Assert.AreEqual(desired, confirmed.ConfirmedState);
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
    public async Task StopRejectsQueuedPlayForTheRemovedBinding()
    {
        await using var fixture = await ITunesNativeFixture.CreateAsync();
        await fixture.RefreshAsync();
        var original = (await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single();
        var play = new MediaBackendCommand(original.Id, original.BindingGeneration, MediaOperation.Play, []);

        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await fixture.ExecuteAsync(MediaOperation.Stop)).Status);
        Assert.AreEqual(MediaBackendCommandStatus.SessionGone, (await fixture.Backend.ExecuteAsync(play, default)).Status);
        Assert.AreEqual(ITPlayerState.Stopped, fixture.PlayerState);

        fixture.HasCurrentTrack = true;
        await fixture.RefreshAsync();
        var replacement = (await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single();
        Assert.IsGreaterThan(original.BindingGeneration, replacement.BindingGeneration);
        Assert.AreEqual(MediaBackendCommandStatus.SessionGone, (await fixture.Backend.ExecuteAsync(play, default)).Status);
        Assert.AreEqual(ITPlayerState.Stopped, fixture.PlayerState);
        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await fixture.ExecuteAsync(MediaOperation.Play)).Status);
        Assert.AreEqual(ITPlayerState.Playing, fixture.PlayerState);
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
        var store = new MediaStateStore();
        store.ApplyBackendSnapshot(await fixture.Backend.ReadSnapshotAsync(default));
        var command = AcceptCommand(store, failing);
        fixture.CommandResult = unchecked((int)0x80004005);

        var result = await fixture.ExecuteAsync(failing);
        Assert.AreEqual(MediaBackendCommandStatus.Failed, result.Status);
        store.CompleteCommand(command, new(1), succeeded: false, result.ConfirmedPlaybackRevision);
        await fixture.RefreshAsync();
        Assert.AreEqual(expected, (await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single().PlaybackState);
        Assert.AreEqual(expected, store.Current.Sessions.Single().PlaybackInfo.EffectiveState);
        Assert.IsFalse(store.Current.Sessions.Single().PlaybackInfo.IsOptimistic);
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

    private static ResolvedMediaCommand AcceptCommand(MediaStateStore store, MediaOperation operation, long operationId = 1)
    {
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted,
            store.TryResolveCommand(new(MediaCommandTarget.CurrentSession, operation), out var command));
        store.ApplyAcceptedCommand(command, new(operationId));
        return command;
    }
}