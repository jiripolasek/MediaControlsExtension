// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Concurrent;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class MediaServiceConcurrencyTests
{
    [TestMethod]
    public async Task CanceledReadinessWaitCompletesDequeuedCommand()
    {
        var work = new MediaService.CommandWork(
            new(1),
            new(
                MediaOperation.Play,
                MediaOperation.Play,
                new(1),
                new(1),
                1,
                [],
                []));
        using var cancellation = new CancellationTokenSource();

        var readinessTask = work.WaitUntilReadyAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await readinessTask);
        var outcome = await work.Completion;
        Assert.AreEqual(MediaCommandOutcomeStatus.Canceled, outcome.Status);
        Assert.AreEqual(new MediaOperationId(1), outcome.OperationId);
        Assert.AreEqual(new MediaSessionId(1), outcome.SessionId);
    }

    [TestMethod]
    public async Task DisposeDuringStartWaitsForStartupAndKeepsStoppedState()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Initial"));
        backend.BlockStart();
        var service = new MediaService(backend);
        var startTask = service.StartAsync();
        await backend.StartStarted;

        var disposeTask = service.DisposeAsync().AsTask();
        await Task.Yield();

        Assert.AreEqual(MediaServiceStatus.Stopped, service.Status);
        Assert.AreEqual(0, backend.DisposeCount);
        Assert.IsFalse(disposeTask.IsCompleted);

        backend.ReleaseStart();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await startTask);
        await disposeTask;
        Assert.AreEqual(MediaServiceStatus.Stopped, service.Status);
        Assert.AreEqual(1, backend.DisposeCount);
    }

    [TestMethod]
    public async Task SlowSessionSubscriberDoesNotBlockStateProduction()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Initial"));
        await using var service = new MediaService(backend);
        await service.StartAsync();

        var session = service.CurrentSession;
        Assert.IsNotNull(session);
        var subscriberEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseSubscriber = new ManualResetEventSlim();
        session.Changed += (_, args) =>
        {
            if (session.MediaProperties.Title == "Track 2")
            {
                subscriberEntered.TrySetResult();
                releaseSubscriber.Wait();
            }
        };

        try
        {
            backend.SetSnapshot(FakeMediaBackend.CreateSnapshot(2, "Track 2"));
            await subscriberEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            for (var revision = 3; revision <= 100; revision++)
            {
                backend.SetSnapshot(FakeMediaBackend.CreateSnapshot(revision, $"Track {revision}"));
            }

            await WaitUntilAsync(() => session.MediaProperties.Title == "Track 100");
            Assert.AreEqual("Track 100", session.MediaProperties.Title);
        }
        finally
        {
            releaseSubscriber.Set();
        }
    }

    [TestMethod]
    public async Task SessionViewKeepsIdentityAndReportsChangedGroup()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Initial"));
        await using var service = new MediaService(backend);
        await service.StartAsync();

        var session = service.CurrentSession;
        Assert.IsNotNull(session);
        var changed = new TaskCompletionSource<MediaSessionChangedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += (_, args) => changed.TrySetResult(args);

        backend.SetSnapshot(FakeMediaBackend.CreateSnapshot(2, "Latest"));

        var args = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreSame(session, service.CurrentSession);
        Assert.AreSame(session, service.Sessions.Single());
        Assert.AreEqual("Latest", session.MediaProperties.Title);
        Assert.AreEqual(MediaSessionChanges.MediaProperties, args.Changes);
    }

    [TestMethod]
    public async Task TimelineUpdatePreservesUnchangedPropertyGroups()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Initial"));
        await using var service = new MediaService(backend);
        await service.StartAsync();

        var session = service.CurrentSession;
        Assert.IsNotNull(session);
        var mediaProperties = session.MediaProperties;
        var playbackInfo = session.PlaybackInfo;
        var changed = new TaskCompletionSource<MediaSessionChangedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += (_, args) => changed.TrySetResult(args);

        backend.SetSnapshot(FakeMediaBackend.CreateSnapshot(
            2,
            "Initial",
            position: TimeSpan.FromSeconds(30)));

        var args = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(MediaSessionChanges.TimelineProperties, args.Changes);
        Assert.AreSame(mediaProperties, session.MediaProperties);
        Assert.AreSame(playbackInfo, session.PlaybackInfo);
        Assert.AreEqual(TimeSpan.FromSeconds(30), session.TimelineProperties.Position);
    }

    [TestMethod]
    public async Task NonCurrentSessionChangeOnlyNotifiesItsKeyedView()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(
            1,
            1,
            (1, "First"),
            (2, "Second")));
        await using var service = new MediaService(backend);
        await service.StartAsync();

        var first = service.Sessions.Single(session => session.Id == new MediaSessionId(1));
        var second = service.Sessions.Single(session => session.Id == new MediaSessionId(2));
        var firstNotifications = 0;
        var secondChanged = new TaskCompletionSource<MediaSessionChangedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        first.Changed += (_, _) => Interlocked.Increment(ref firstNotifications);
        second.Changed += (_, args) => secondChanged.TrySetResult(args);

        backend.SetSnapshot(FakeMediaBackend.CreateSnapshot(
            2,
            1,
            (1, "First"),
            (2, "Updated")));

        var args = await secondChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(MediaSessionChanges.MediaProperties, args.Changes);
        Assert.AreEqual(0, Volatile.Read(ref firstNotifications));
        Assert.AreSame(first, service.Sessions[0]);
        Assert.AreSame(second, service.Sessions[1]);
    }

    [TestMethod]
    public async Task RemovedSessionViewBecomesUnavailable()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(
            1,
            1,
            (1, "First"),
            (2, "Second")));
        await using var service = new MediaService(backend);
        await service.StartAsync();

        var removed = service.Sessions.Single(session => session.Id == new MediaSessionId(2));
        var removedChanged = new TaskCompletionSource<MediaSessionChangedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        removed.Changed += (_, args) => removedChanged.TrySetResult(args);

        backend.SetSnapshot(FakeMediaBackend.CreateSnapshot(
            2,
            1,
            (1, "First")));

        var args = await removedChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(MediaSessionChanges.Availability, args.Changes);
        Assert.IsFalse(removed.IsAvailable);
        Assert.IsFalse(service.Sessions.Contains(removed));
    }

    [TestMethod]
    public async Task CurrentSessionChangedReportsTheLatestCurrentSession()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(
            1,
            1,
            (1, "First"),
            (2, "Second")));
        await using var service = new MediaService(backend);
        var initialNotification = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var changedNotification = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationCount = 0;
        service.CurrentSessionChanged += (_, _) =>
        {
            if (Interlocked.Increment(ref notificationCount) == 1)
            {
                initialNotification.TrySetResult();
            }
            else
            {
                changedNotification.TrySetResult();
            }
        };

        await service.StartAsync();
        await initialNotification.Task.WaitAsync(TimeSpan.FromSeconds(5));

        backend.SetSnapshot(FakeMediaBackend.CreateSnapshot(
            2,
            2,
            (1, "First"),
            (2, "Second")));

        await changedNotification.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(new MediaSessionId(2), service.CurrentSession?.Id);
        Assert.AreEqual(2, Volatile.Read(ref notificationCount));
    }

    [TestMethod]
    public async Task ConcurrentReadersObserveCompleteSessionStates()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Initial"));
        await using var service = new MediaService(backend);
        await service.StartAsync();

        var failures = new ConcurrentQueue<string>();
        var readers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(
                () =>
                {
                    for (var index = 0; index < 2_000; index++)
                    {
                        var session = service.CurrentSession;
                        if (session is null)
                        {
                            continue;
                        }

                        var playbackInfo = session.PlaybackInfo;
                        if (playbackInfo.IsOptimistic &&
                            playbackInfo.EffectiveState == playbackInfo.ConfirmedState)
                        {
                            failures.Enqueue($"Revision {session.Revision} published an inconsistent playback prediction.");
                        }
                    }
                }))
            .ToArray();

        for (var revision = 2; revision <= 100; revision++)
        {
            backend.SetSnapshot(FakeMediaBackend.CreateSnapshot(revision, $"Track {revision}"));
        }

        await Task.WhenAll(readers);
        Assert.IsTrue(failures.IsEmpty, string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    public async Task CommandMailboxAllowsOneActiveAndOnePendingCommand()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Initial"));
        backend.BlockCommands();
        var timeProvider = new ManualTimeProvider();
        await using var service = new MediaService(backend, timeProvider: timeProvider);
        await service.StartAsync();

        var command = new MediaCommand(MediaCommandTarget.CurrentSession, MediaOperation.Play);
        var active = service.TrySubmit(command);
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, active.Status);
        await backend.CommandStarted;

        timeProvider.Advance(TimeSpan.FromMilliseconds(200));
        var pending = service.TrySubmit(command);
        timeProvider.Advance(TimeSpan.FromMilliseconds(200));
        var rejected = service.TrySubmit(command);
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, pending.Status);
        Assert.AreEqual(MediaCommandSubmissionStatus.Busy, rejected.Status);

        backend.ReleaseCommands();
        Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await active.Completion!).Status);
        Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await pending.Completion!).Status);
    }

    [TestMethod]
    public async Task PlaybackAdmissionThrottlesRepeatedInputAndUsesOptimisticToggleState()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Initial"));
        var timeProvider = new ManualTimeProvider();
        await using var service = new MediaService(backend, timeProvider: timeProvider);
        await service.StartAsync();

        var command = new MediaCommand(
            MediaCommandTarget.CurrentSession,
            MediaOperation.TogglePlayback);
        var play = service.TrySubmit(command);
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, play.Status);
        Assert.AreEqual(
            MediaPlaybackState.Playing,
            service.CurrentSession?.PlaybackInfo.EffectiveState);
        Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await play.Completion!).Status);

        var throttled = service.TrySubmit(command);
        Assert.AreEqual(MediaCommandSubmissionStatus.Busy, throttled.Status);
        Assert.AreEqual(
            MediaPlaybackState.Playing,
            service.CurrentSession?.PlaybackInfo.EffectiveState);

        timeProvider.Advance(TimeSpan.FromMilliseconds(200));
        var pause = service.TrySubmit(command);
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, pause.Status);
        Assert.AreEqual(
            MediaPlaybackState.Paused,
            service.CurrentSession?.PlaybackInfo.EffectiveState);
        Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await pause.Completion!).Status);
    }

    [TestMethod]
    public async Task FailedPlaybackCommandRollsBackOptimisticState()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Initial"))
        {
            CommandResult = new(MediaBackendCommandStatus.Failed, "Rejected by test backend."),
        };
        backend.BlockCommands();
        await using var service = new MediaService(backend);
        await service.StartAsync();

        var submission = service.TrySubmit(new(
            MediaCommandTarget.CurrentSession,
            MediaOperation.Play));
        Assert.AreEqual(MediaPlaybackState.Playing, service.CurrentSession?.PlaybackInfo.EffectiveState);
        Assert.IsTrue(service.CurrentSession?.PlaybackInfo.IsOptimistic);

        backend.ReleaseCommands();
        Assert.AreEqual(MediaCommandOutcomeStatus.Failed, (await submission.Completion!).Status);
        Assert.AreEqual(MediaPlaybackState.Paused, service.CurrentSession?.PlaybackInfo.EffectiveState);
        Assert.IsFalse(service.CurrentSession?.PlaybackInfo.IsOptimistic);
    }

    [TestMethod]
    public async Task ConfirmedPredictionDoesNotRequestRefreshWhenItsLifetimeEnds()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Initial"));
        await using var service = new MediaService(
            backend,
            playbackPredictionLifetime: TimeSpan.FromMilliseconds(500));
        await service.StartAsync();
        await WaitUntilSnapshotReadsSettleAsync(backend);

        var submission = service.TrySubmit(new(
            MediaCommandTarget.CurrentSession,
            MediaOperation.Play));
        Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await submission.Completion!).Status);

        backend.SetSnapshot(FakeMediaBackend.CreateSnapshot(
            2,
            "Initial",
            playbackState: MediaPlaybackState.Playing));
        await WaitUntilAsync(
            () => service.CurrentSession?.PlaybackInfo is
            {
                ConfirmedState: MediaPlaybackState.Playing,
                IsOptimistic: false,
            });
        await WaitUntilAsync(() => !backend.ObservationInvalidations.IsEmpty);
        await WaitUntilSnapshotReadsSettleAsync(backend);
        var readsAfterConfirmation = backend.SnapshotReadCount;

        await Task.Delay(TimeSpan.FromMilliseconds(600));

        Assert.AreEqual(readsAfterConfirmation, backend.SnapshotReadCount);
    }

    [TestMethod]
    public async Task UnconfirmedPredictionExpiresAndRequestsRefresh()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Initial"));
        await using var service = new MediaService(
            backend,
            playbackPredictionLifetime: TimeSpan.FromMilliseconds(100));
        await service.StartAsync();
        await WaitUntilSnapshotReadsSettleAsync(backend);

        var submission = service.TrySubmit(new(
            MediaCommandTarget.CurrentSession,
            MediaOperation.Play));
        Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await submission.Completion!).Status);
        Assert.IsTrue(service.CurrentSession?.PlaybackInfo.IsOptimistic);
        var readsBeforeExpiration = backend.SnapshotReadCount;

        await WaitUntilAsync(
            () => service.CurrentSession?.PlaybackInfo.IsOptimistic == false &&
                  backend.SnapshotReadCount > readsBeforeExpiration);

        Assert.IsGreaterThan(readsBeforeExpiration, backend.SnapshotReadCount);
    }

    [TestMethod]
    public async Task NavigationAdmissionThrottlesAcrossDirectionsWithoutBlockingPlayback()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Initial"));
        var timeProvider = new ManualTimeProvider();
        await using var service = new MediaService(backend, timeProvider: timeProvider);
        await service.StartAsync();

        var next = service.TrySubmit(new(
            MediaCommandTarget.CurrentSession,
            MediaOperation.SkipNext));
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, next.Status);
        Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await next.Completion!).Status);

        var immediatePrevious = service.TrySubmit(new(
            MediaCommandTarget.CurrentSession,
            MediaOperation.SkipPrevious));
        Assert.AreEqual(MediaCommandSubmissionStatus.Busy, immediatePrevious.Status);

        var play = service.TrySubmit(new(
            MediaCommandTarget.CurrentSession,
            MediaOperation.Play));
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, play.Status);
        Assert.AreEqual(
            MediaPlaybackState.Playing,
            service.CurrentSession?.PlaybackInfo.EffectiveState);
        Assert.IsTrue(service.CurrentSession?.PlaybackInfo.IsOptimistic);
        Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await play.Completion!).Status);

        timeProvider.Advance(TimeSpan.FromMilliseconds(199));
        var stillThrottled = service.TrySubmit(new(
            MediaCommandTarget.CurrentSession,
            MediaOperation.SkipPrevious));
        Assert.AreEqual(MediaCommandSubmissionStatus.Busy, stillThrottled.Status);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        var previous = service.TrySubmit(new(
            MediaCommandTarget.CurrentSession,
            MediaOperation.SkipPrevious));
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, previous.Status);
        Assert.AreEqual(3L, previous.OperationId.Value);
        Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await previous.Completion!).Status);
    }

    [TestMethod]
    public async Task SuccessfulNavigationRefreshesAgainAfterInputSettles()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Initial"));
        await using var service = new MediaService(backend);
        await service.StartAsync();
        await WaitUntilSnapshotReadsSettleAsync(backend);

        backend.SetSnapshotWithoutSignal(FakeMediaBackend.CreateSnapshot(2, "Intermediate"));
        var readsBeforeCommand = backend.SnapshotReadCount;
        var submission = service.TrySubmit(new(
            MediaCommandTarget.CurrentSession,
            MediaOperation.SkipNext));
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, submission.Status);
        Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await submission.Completion!).Status);
        await WaitUntilAsync(
            () => backend.SnapshotReadCount > readsBeforeCommand &&
                  service.CurrentSession?.MediaProperties.Title == "Intermediate");

        backend.SetSnapshotWithoutSignal(FakeMediaBackend.CreateSnapshot(3, "Settled"));
        await WaitUntilAsync(
            () => service.CurrentSession?.MediaProperties.Title == "Settled");

        var invalidation = backend.ObservationInvalidations.Single();
        var request = invalidation.Single();
        Assert.AreEqual(new MediaBackendSessionId(1), request.SessionId);
        Assert.AreEqual(
            MediaBackendObservationChanges.Playback |
            MediaBackendObservationChanges.Timeline,
            request.Changes);
    }

    [TestMethod]
    public async Task SuccessfulPlayReconcilesOnlyTargetAndPreviouslyPlayingSessions()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(
            1,
            1,
            (1, "Target", MediaPlaybackState.Paused),
            (2, "Playing", MediaPlaybackState.Playing),
            (3, "Paused", MediaPlaybackState.Paused)));
        await using var service = new MediaService(backend);
        await service.StartAsync();
        await WaitUntilSnapshotReadsSettleAsync(backend);

        var submission = service.TrySubmit(new(
            MediaCommandTarget.CurrentSession,
            MediaOperation.Play));
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, submission.Status);
        Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await submission.Completion!).Status);
        await WaitUntilAsync(() => !backend.ObservationInvalidations.IsEmpty);

        var requests = backend.ObservationInvalidations
            .Single()
            .ToDictionary(static request => request.SessionId);
        Assert.AreEqual(2, requests.Count);
        Assert.AreEqual(
            MediaBackendObservationChanges.Playback,
            requests[new MediaBackendSessionId(1)].Changes);
        Assert.AreEqual(
            MediaBackendObservationChanges.Playback,
            requests[new MediaBackendSessionId(2)].Changes);
        Assert.IsFalse(requests.ContainsKey(new MediaBackendSessionId(3)));
    }

    [TestMethod]
    public async Task ReboundSessionDropsPredictionFromOldBindingGeneration()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Initial"));
        backend.BlockCommands();
        await using var service = new MediaService(backend);
        await service.StartAsync();

        var submission = service.TrySubmit(new(
            MediaCommandTarget.CurrentSession,
            MediaOperation.Play));
        await backend.CommandStarted;
        var session = service.CurrentSession;
        Assert.IsNotNull(session);
        Assert.IsTrue(session.PlaybackInfo.IsOptimistic);
        var rebound = new TaskCompletionSource<MediaSessionChangedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += (_, args) =>
        {
            if (args.Changes.HasFlag(MediaSessionChanges.Rebound))
            {
                rebound.TrySetResult(args);
            }
        };

        backend.SetSnapshot(FakeMediaBackend.CreateSnapshot(
            2,
            "Rebound",
            bindingGeneration: 2,
            playbackState: MediaPlaybackState.Paused));
        await rebound.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreSame(session, service.CurrentSession);
        Assert.IsFalse(session.PlaybackInfo.IsOptimistic);
        Assert.AreEqual(MediaPlaybackState.Paused, session.PlaybackInfo.EffectiveState);

        backend.ReleaseCommands();
        Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await submission.Completion!).Status);
        Assert.IsFalse(session.PlaybackInfo.IsOptimistic);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private static async Task WaitUntilSnapshotReadsSettleAsync(FakeMediaBackend backend)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var previousCount = backend.SnapshotReadCount;
        while (true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
            var currentCount = backend.SnapshotReadCount;
            if (currentCount == previousCount)
            {
                return;
            }

            previousCount = currentCount;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref this._timestamp);

        public void Advance(TimeSpan elapsed)
        {
            Interlocked.Add(ref this._timestamp, elapsed.Ticks);
        }
    }
}