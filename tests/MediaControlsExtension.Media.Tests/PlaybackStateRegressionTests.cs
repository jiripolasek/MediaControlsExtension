// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Gsmtc;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.State;
using JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class PlaybackStateRegressionTests
{
    [TestMethod]
    public async Task PauseEndingInStoppedAllowsTheQueuedPlayToRun()
    {
        var snapshot = FakeMediaBackend.CreateSnapshot(1, "Player", playbackState: MediaPlaybackState.Playing);
        snapshot = snapshot with { Sessions = [snapshot.Sessions[0] with { Capabilities = MediaCapabilities.Pause }] };
        var backend = new FakeMediaBackend(snapshot);
        backend.BlockCommands();
        var controller = new GsmtcPlaybackController();
        var observations = new GsmtcPlaybackObservations(new Lock());
        var controls = new GsmtcControlGate(NullLogger.Instance);
        var state = MediaPlaybackState.Playing;
        var nativeOperations = new List<MediaOperation>();
        GsmtcPlaybackController.PlaybackObservation Observe() =>
            new(state, state == MediaPlaybackState.Playing ? MediaCapabilities.Pause : MediaCapabilities.Play);
        backend.CommandHandler = (command, cancellationToken) => controller.ExecuteAsync(command.Operation,
            observations,
            () => Task.FromResult(Observe()),
            (markSending, token) => controls.RunCommandAsync(() =>
                GsmtcPlaybackController.SendRevalidatedAsync(command.Operation, observations, observations.ReadCommand(Observe), operation =>
                {
                    nativeOperations.Add(operation);
                    state = operation == MediaOperation.Pause ? MediaPlaybackState.Stopped : MediaPlaybackState.Playing;
                    snapshot = snapshot with
                    {
                        Revision = snapshot.Revision + 1,
                        Sessions = [snapshot.Sessions[0] with { PlaybackState = state, Capabilities = Observe().Capabilities }],
                    };
                    backend.SetSnapshot(snapshot);
                    observations.Invalidate();
                    return Task.FromResult(true);
                }, markSending), "Playback", token), cancellationToken);
        await using var service = new MediaService(backend);
        await service.StartAsync();
        try
        {
            var pause = service.TrySubmit(new(MediaCommandTarget.CurrentSession, MediaOperation.Pause));
            await backend.CommandStarted.WaitAsync(TimeSpan.FromSeconds(2));
            var play = service.TrySubmit(new(MediaCommandTarget.CurrentSession, MediaOperation.Play));
            Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, play.Status);
            Assert.IsFalse(play.Completion!.IsCompleted);

            backend.ReleaseCommands();
            var outcomes = await Task.WhenAll(pause.Completion!, play.Completion).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.IsTrue(outcomes.All(static outcome => outcome.Status == MediaCommandOutcomeStatus.Completed));
            CollectionAssert.AreEqual(new[] { MediaOperation.Pause, MediaOperation.Play }, nativeOperations);
            Assert.AreEqual(MediaPlaybackState.Playing, state);
        }
        finally
        {
            backend.ReleaseCommands();
        }
    }

    [TestMethod]
    [DataRow(MediaOperation.Play, MediaCapabilities.Play)]
    [DataRow(MediaOperation.Play, MediaCapabilities.Play | MediaCapabilities.Stop)]
    [DataRow(MediaOperation.Pause, MediaCapabilities.Pause)]
    public void PendingDirectionalIntentAdmitsItsOppositeWithoutChangingCapabilities(
        MediaOperation intent, MediaCapabilities capabilities)
    {
        var initialState = intent == MediaOperation.Play ? MediaPlaybackState.Paused : MediaPlaybackState.Playing;
        var opposite = intent == MediaOperation.Play ? MediaOperation.Pause : MediaOperation.Play;
        var snapshot = FakeMediaBackend.CreateSnapshot(1, 1,
            (1, "Current", MediaPlaybackState.Paused), (2, "Transition", initialState));
        snapshot = snapshot with
        {
            Sessions = [snapshot.Sessions[0], snapshot.Sessions[1] with { Capabilities = capabilities }],
        };
        var store = new MediaStateStore();
        store.ApplyBackendSnapshot(snapshot);
        var target = MediaCommandTarget.ForSession(new(2));
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, store.TryResolveCommand(new(target, intent), out var pending));
        store.ApplyAcceptedCommand(pending, new(1));
        Assert.AreEqual(opposite, store.Current.Sessions[1].PlaybackInfo.PrimaryOperation);
        store.ApplyBackendSnapshot(snapshot with { Revision = 2 });

        Assert.AreEqual(capabilities, store.Current.Sessions[1].PlaybackInfo.Capabilities);
        Assert.AreEqual(opposite, store.Current.Sessions[1].PlaybackInfo.PrimaryOperation);
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, store.TryResolveCommand(new(target, opposite), out _));
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted,
            store.TryResolveCommand(new(target, MediaOperation.TogglePlayback), out var relative));
        Assert.AreEqual(opposite, relative.ResolvedOperation);
        if (opposite == MediaOperation.Play)
        {
            Assert.AreEqual(MediaCommandSubmissionStatus.Accepted,
                store.TryResolveCommand(new(MediaCommandTarget.ForSession(new(1)), MediaOperation.SwitchNextSession), out _));
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SuccessfulPauseReconcilesAStoppedObservation(bool observedBeforeCompletion)
    {
        var snapshot = FakeMediaBackend.CreateSnapshot(1, "Player", playbackState: MediaPlaybackState.Playing);
        var stopped = snapshot with
        {
            Revision = 2,
            Sessions = [snapshot.Sessions[0] with { PlaybackState = MediaPlaybackState.Stopped }],
        };
        var store = new MediaStateStore();
        store.ApplyBackendSnapshot(snapshot);
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted,
            store.TryResolveCommand(new(MediaCommandTarget.CurrentSession, MediaOperation.Pause), out var pause));
        store.ApplyAcceptedCommand(pause, new(1));
        if (observedBeforeCompletion)
        {
            store.ApplyBackendSnapshot(stopped);
            Assert.IsTrue(store.Current.Sessions[0].PlaybackInfo.IsOptimistic);
        }

        store.CompleteCommand(pause, new(1), succeeded: true);
        if (!observedBeforeCompletion)
        {
            Assert.IsTrue(store.Current.Sessions[0].PlaybackInfo.IsOptimistic);
            store.ApplyBackendSnapshot(stopped);
        }

        Assert.IsFalse(store.Current.Sessions[0].PlaybackInfo.IsOptimistic);
        Assert.AreEqual(MediaPlaybackState.Stopped, store.Current.Sessions[0].PlaybackInfo.EffectiveState);
        Assert.AreEqual(MediaOperation.Play, store.Current.Sessions[0].PlaybackInfo.PrimaryOperation);
    }

    [TestMethod]
    [DataRow(MediaPlaybackState.Unknown)]
    [DataRow(MediaPlaybackState.Changing)]
    [DataRow(MediaPlaybackState.Opened)]
    [DataRow(MediaPlaybackState.Closed)]
    public void SuccessfulPauseRetainsPredictionUntilPlaybackSettles(MediaPlaybackState observedState)
    {
        var snapshot = FakeMediaBackend.CreateSnapshot(1, "Player", playbackState: MediaPlaybackState.Playing);
        var store = new MediaStateStore();
        store.ApplyBackendSnapshot(snapshot);
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted,
            store.TryResolveCommand(new(MediaCommandTarget.CurrentSession, MediaOperation.Pause), out var pause));
        store.ApplyAcceptedCommand(pause, new(1));
        store.CompleteCommand(pause, new(1), succeeded: true);

        store.ApplyBackendSnapshot(snapshot with
        {
            Revision = 2,
            Sessions = [snapshot.Sessions[0] with { PlaybackState = observedState }],
        });

        Assert.IsTrue(store.Current.Sessions[0].PlaybackInfo.IsOptimistic);
        Assert.AreEqual(MediaPlaybackState.Paused, store.Current.Sessions[0].PlaybackInfo.EffectiveState);
    }

    [TestMethod]
    [DataRow(MediaPlaybackState.Changing, MediaOperation.Play)]
    [DataRow(MediaPlaybackState.Unknown, MediaOperation.Play)]
    [DataRow(MediaPlaybackState.Stopped, MediaOperation.Play)]
    [DataRow(MediaPlaybackState.Changing, MediaOperation.Pause)]
    [DataRow(MediaPlaybackState.Unknown, MediaOperation.Pause)]
    [DataRow(MediaPlaybackState.Stopped, MediaOperation.Pause)]
    public void PendingToggleIntentRemainsCorrectableDuringATransientObservation(MediaPlaybackState transientState, MediaOperation pendingOperation)
    {
        var initialState = pendingOperation == MediaOperation.Play ? MediaPlaybackState.Paused : MediaPlaybackState.Playing;
        var snapshot = FakeMediaBackend.CreateSnapshot(1, 1,
            (1, "Current", MediaPlaybackState.Paused), (2, "Transition", initialState));
        snapshot = snapshot with
        {
            Sessions = [snapshot.Sessions[0], snapshot.Sessions[1] with { Capabilities = MediaCapabilities.TogglePlayback }],
        };
        var store = new MediaStateStore();
        store.UpdateOptions(new(PauseOtherSessionsOnPlay: true));
        store.ApplyBackendSnapshot(snapshot);
        var target = MediaCommandTarget.ForSession(new(2));
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, store.TryResolveCommand(new(target, pendingOperation), out var pending));
        store.ApplyAcceptedCommand(pending, new(1));
        store.ApplyBackendSnapshot(snapshot with
        {
            Revision = 2,
            Sessions = [snapshot.Sessions[0], snapshot.Sessions[1] with { PlaybackState = transientState }],
        });
        var displayedAction = store.Current.Sessions[1].PlaybackInfo.PrimaryOperation;
        Assert.AreEqual(pendingOperation == MediaOperation.Play ? MediaOperation.Pause : MediaOperation.Play, displayedAction);

        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted,
            store.TryResolveCommand(new(target, displayedAction), out _));
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted,
            store.TryResolveCommand(new(MediaCommandTarget.ForSession(new(1)), MediaOperation.SwitchNextSession), out var navigation));
        Assert.AreEqual(new MediaSessionId(2), navigation.SessionId);
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted,
            store.TryResolveCommand(new(MediaCommandTarget.ForSession(new(1)), MediaOperation.Play), out var playOther));
        Assert.AreEqual(pendingOperation == MediaOperation.Play ? 1 : 0, playOther.SessionsToPause.Length);
    }

    [TestMethod]
    public async Task PredictionLifetimeStartsAfterQueuedPlaybackCompletes()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Player"));
        backend.BlockCommands();
        await using var service = new MediaService(backend, playbackPredictionLifetime: TimeSpan.FromMilliseconds(50));
        await service.StartAsync();
        try
        {
            var play = service.TrySubmit(new(MediaCommandTarget.CurrentSession, MediaOperation.Play));
            await backend.CommandStarted.WaitAsync(TimeSpan.FromSeconds(2));
            var pause = service.TrySubmit(new(MediaCommandTarget.CurrentSession, MediaOperation.Pause));
            Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, pause.Status);

            await Task.Delay(200);

            Assert.IsFalse(play.Completion!.IsCompleted);
            Assert.IsFalse(pause.Completion!.IsCompleted);
            Assert.IsTrue(service.CurrentSession!.PlaybackInfo.IsOptimistic);
            Assert.AreEqual(MediaPlaybackState.Paused, service.CurrentSession.PlaybackInfo.EffectiveState);
            backend.ReleaseCommands();
            await Task.WhenAll(play.Completion, pause.Completion).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsFalse(service.CurrentSession.PlaybackInfo.IsOptimistic);
        }
        finally
        {
            backend.ReleaseCommands();
        }
    }

    [TestMethod]
    public void CompletionClearsAPredictionThatWasConfirmedWhileTheCommandWasRunning()
    {
        var snapshot = FakeMediaBackend.CreateSnapshot(1, "Player");
        var store = new MediaStateStore();
        store.ApplyBackendSnapshot(snapshot);
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted,
            store.TryResolveCommand(new(MediaCommandTarget.CurrentSession, MediaOperation.Play), out var play));
        store.ApplyAcceptedCommand(play, new(1));
        var confirmed = store.ApplyBackendSnapshot(snapshot with
        {
            Revision = 2,
            Sessions = [snapshot.Sessions[0] with { PlaybackState = MediaPlaybackState.Playing }],
        });
        Assert.IsTrue(confirmed.Sessions[0].PlaybackInfo.IsOptimistic);

        var completed = store.CompleteCommand(play, new(1), succeeded: true);

        Assert.IsFalse(completed.Sessions[0].PlaybackInfo.IsOptimistic);
        Assert.AreEqual(MediaPlaybackState.Playing, completed.Sessions[0].PlaybackInfo.EffectiveState);
    }

    [TestMethod]
    [DataRow(MediaPlaybackState.Unknown)]
    [DataRow(MediaPlaybackState.Stopped)]
    [DataRow(MediaPlaybackState.Changing)]
    public void ToggleOnlyPlaybackRequiresAKnownStateAtAdmission(MediaPlaybackState state)
    {
        var snapshot = FakeMediaBackend.CreateSnapshot(1, 1, (1, "Current"), (2, "Toggle only"));
        var store = new MediaStateStore();
        store.ApplyBackendSnapshot(snapshot with
        {
            Sessions = [snapshot.Sessions[0], snapshot.Sessions[1] with
            {
                PlaybackState = state,
                Capabilities = MediaCapabilities.TogglePlayback,
            }],
        });

        Assert.AreEqual(MediaCommandSubmissionStatus.Unsupported,
            store.TryResolveCommand(new(MediaCommandTarget.ForSession(new(2)), MediaOperation.Play), out _));
        Assert.AreEqual(MediaCommandSubmissionStatus.Unsupported,
            store.TryResolveCommand(new(MediaCommandTarget.ForSession(new(2)), MediaOperation.Pause), out _));
        Assert.AreEqual(MediaCommandSubmissionStatus.Unsupported,
            store.TryResolveCommand(new(MediaCommandTarget.CurrentSession, MediaOperation.SwitchNextSession), out _));
        Assert.IsFalse(store.Current.Sessions[1].PlaybackInfo.IsOptimistic);
    }

    [TestMethod]
    public void AutomaticPauseSkipsInactivePlayersButIncludesUnconfirmedPlaybackTransitions()
    {
        var snapshot = FakeMediaBackend.CreateSnapshot(1, 1,
            (1, "Target", MediaPlaybackState.Paused),
            (2, "Pausing", MediaPlaybackState.Playing),
            (3, "Paused", MediaPlaybackState.Paused),
            (4, "Stopped", MediaPlaybackState.Stopped),
            (5, "Unknown", MediaPlaybackState.Unknown),
            (6, "Starting", MediaPlaybackState.Paused),
            (7, "Cannot pause", MediaPlaybackState.Playing));
        var store = new MediaStateStore();
        store.UpdateOptions(new(PauseOtherSessionsOnPlay: true));
        store.ApplyBackendSnapshot(snapshot with
        {
            Sessions = [snapshot.Sessions[0], snapshot.Sessions[1],
                snapshot.Sessions[2] with { Capabilities = MediaCapabilities.TogglePlayback },
                snapshot.Sessions[3], snapshot.Sessions[4],
                snapshot.Sessions[5] with { Capabilities = MediaCapabilities.Play },
                snapshot.Sessions[6] with { Capabilities = MediaCapabilities.Play }],
        });
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted,
            store.TryResolveCommand(new(MediaCommandTarget.ForSession(new(2)), MediaOperation.Pause), out var pause));
        store.ApplyAcceptedCommand(pause, new(1));
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted,
            store.TryResolveCommand(new(MediaCommandTarget.ForSession(new(6)), MediaOperation.Play), out var starting));
        store.ApplyAcceptedCommand(starting, new(2));

        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted,
            store.TryResolveCommand(new(MediaCommandTarget.ForSession(new(1)), MediaOperation.Play), out var play));

        CollectionAssert.AreEquivalent(new long[] { 2, 6 }, play.SessionsToPause.Select(static target => target.SessionId.Value).ToArray());
    }

    [TestMethod]
    public async Task PlayReplacesQueuedPauseAndSelectsTheRequestedSession()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, 1, (1, "Current"), (2, "Target")));
        backend.BlockCommands();
        await using var service = new MediaService(backend);
        await service.StartAsync();
        var target = MediaCommandTarget.ForSession(new(2));
        try
        {
            var skip = service.TrySubmit(new(target, MediaOperation.SkipNext));
            await backend.CommandStarted.WaitAsync(TimeSpan.FromSeconds(2));
            var pause = service.TrySubmit(new(target, MediaOperation.Pause));
            Assert.AreEqual(new MediaSessionId(1), service.CurrentSession?.Id);

            var play = service.TrySubmit(new(target, MediaOperation.Play));

            Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, play.Status);
            Assert.AreEqual(MediaCommandOutcomeStatus.Superseded, (await pause.Completion!.WaitAsync(TimeSpan.FromSeconds(2))).Status);
            Assert.AreEqual(new MediaSessionId(2), service.CurrentSession?.Id);
            backend.ReleaseCommands();
            await Task.WhenAll(skip.Completion!, play.Completion!).WaitAsync(TimeSpan.FromSeconds(2));
            CollectionAssert.AreEqual(new[] { MediaOperation.SkipNext, MediaOperation.Play },
                backend.Commands.Select(static command => command.Operation).ToArray());
        }
        finally
        {
            backend.ReleaseCommands();
        }
    }
}