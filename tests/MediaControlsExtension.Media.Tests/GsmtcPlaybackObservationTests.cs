// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Gsmtc;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Observation = JPSoftworks.MediaControlsExtension.Media.Gsmtc.GsmtcPlaybackController.PlaybackObservation;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class GsmtcPlaybackObservationTests
{
    private static readonly int[] StalledReadEvents = [7, 9, 10];

    [TestMethod]
    public async Task SnapshotCompletionWinsWhenTheTimeoutBecomesReadyAtTheSameTime()
    {
        var entered = PlaybackTestSession.Signal();
        var release = new TaskCompletionSource<Observation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var signals = 0;
        GsmtcPlaybackObservations? observations = null;
        observations = new(new Lock(), TimeSpan.FromMilliseconds(1), TimeSpan.Zero,
            () => signals++, delayUntilTimeout: async (timeout, _, token) =>
            {
                if (timeout != TimeSpan.FromMilliseconds(1))
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return;
                }

                await entered.Task.WaitAsync(token);
                release.TrySetResult(new(MediaPlaybackState.Paused, MediaCapabilities.Play));
                await observations!.DrainPendingReadAsync(token);
            });
        var sample = await observations.ReadSnapshotAsync(() =>
        {
            entered.TrySetResult();
            return release.Task;
        }, default);
        Assert.AreEqual(MediaPlaybackState.Paused, sample.Value.State);
        Assert.IsFalse(observations.IsSnapshotSuspended);
        Assert.AreEqual(0, signals);
    }

    [TestMethod]
    public async Task HungReadSuspendsSnapshotsWithoutALoggerOrSnapshotWaiter()
    {
        var observations = new GsmtcPlaybackObservations(new Lock(), TimeSpan.FromMilliseconds(30), TimeSpan.Zero,
            delayUntilTimeout: (_, _, _) => Task.CompletedTask);
        var entered = PlaybackTestSession.Signal();
        var release = new TaskCompletionSource<Observation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = observations.ReadFreshAsync(() =>
        {
            entered.TrySetResult();
            return release.Task;
        }, 0, 0, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsTrue(observations.IsSnapshotSuspended);
            Assert.IsFalse(observations.NeedsSnapshotRead);
            Assert.IsFalse(read.IsCompleted);
            var cached = await observations.ReadSnapshotAsync(
                () => throw new AssertFailedException("A stalled binding must not start another native read."), default);
            Assert.AreEqual(0L, cached.Sequence);

            release.TrySetResult(new(MediaPlaybackState.Paused, MediaCapabilities.Play));
            Assert.AreEqual(MediaPlaybackState.Paused, (await read).Value.State);
            Assert.IsFalse(observations.IsSnapshotSuspended);
        }
        finally
        {
            release.TrySetResult(new(MediaPlaybackState.Paused, MediaCapabilities.Play));
            await read;
        }
    }

    [TestMethod]
    public async Task HungFallbackLogsSlowPausedAndResumedWithoutASnapshotWaiter()
    {
        var slow = PlaybackTestSession.Signal();
        var stalled = PlaybackTestSession.Signal();
        var logger = new ObservationLogger();
        var observations = new GsmtcPlaybackObservations(new Lock(), TimeSpan.FromMilliseconds(30), TimeSpan.Zero,
            logger: logger, delayUntilTimeout: (timeout, _, token) =>
                (timeout == TimeSpan.FromSeconds(2) ? slow.Task : stalled.Task).WaitAsync(token));
        var session = new PlaybackTestSession
        {
            Observations = observations,
            Controller = new(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(20)),
            RaiseEventOnSend = false,
        };
        var entered = PlaybackTestSession.Signal();
        var release = new TaskCompletionSource<Observation>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.SharedRead = () =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        var command = session.ExecuteAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            slow.TrySetResult();
            await logger.Slow.Task.WaitAsync(TimeSpan.FromSeconds(2));
            stalled.TrySetResult();
            await logger.Paused.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsTrue(observations.IsSnapshotSuspended);
            Assert.IsFalse(release.Task.IsCompleted);
            Assert.AreEqual(MediaBackendCommandStatus.Unconfirmed, (await command).Status);
            Assert.AreEqual(2, session.Reads);
            Assert.HasCount(1, session.Operations);
            release.TrySetResult(session.Value);
            await logger.Resumed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            CollectionAssert.AreEqual(StalledReadEvents, logger.Events.ToArray());
        }
        finally
        {
            release.TrySetResult(session.Value);
        }
    }

    [TestMethod]
    public async Task UnchangedOrdinarySnapshotsMakeNoPlaybackCalls()
    {
        var session = new PlaybackTestSession();
        await session.SnapshotAsync();
        var baseline = session.Reads;
        for (var i = 0; i < 10; i++)
        {
            await session.SnapshotAsync();
        }

        Assert.AreEqual(baseline, session.Reads);
    }

    [TestMethod]
    public async Task SnapshotPublicationsDoNotRequestAnotherRefreshButCommandAndConfirmationPublicationsDo()
    {
        var signals = 0;
        var confirmed = PlaybackTestSession.Signal();
        var observations = new GsmtcPlaybackObservations(new Lock(), () =>
        {
            if (Interlocked.Increment(ref signals) == 2)
            {
                confirmed.TrySetResult();
            }
        });
        await observations.ReadSnapshotAsync(() => Task.FromResult(new Observation(MediaPlaybackState.Playing, MediaCapabilities.Pause)), default);
        Assert.AreEqual(0, signals);

        var command = observations.ReadCommand(() => new(MediaPlaybackState.Playing, MediaCapabilities.Pause));
        Assert.AreEqual(1, signals);
        observations.Invalidate();
        await observations.WaitForObservationAsync(command.Sequence,
            () => Task.FromResult(new Observation(MediaPlaybackState.Paused, MediaCapabilities.Play)), default);
        await confirmed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(2, signals);
        await observations.ReadSnapshotAsync(() => throw new AssertFailedException("The observation is current."), default);
        Assert.AreEqual(2, signals);
    }

    [TestMethod]
    public void BindingLockHeldByTheCallerCanReenterObservationMethods()
    {
        // SessionBinding shares its state lock and calls these members while holding it.
        var bindingLock = new Lock();
        var observations = new GsmtcPlaybackObservations(bindingLock);
        var snapshot = FakeMediaBackend.CreateSnapshot(1, "Player").Sessions[0];
        lock (bindingLock)
        {
            observations.Invalidate();
            Assert.IsTrue(observations.IsDirty);
            Assert.AreEqual(snapshot, observations.Merge(snapshot, MediaCapabilities.None));
            observations.Retire();
        }

        Assert.ThrowsExactly<GsmtcSessionRetiredException>(() => observations.CaptureSequence());
    }

    [TestMethod]
    public async Task AReadFinishingAfterCancellationStillPublishesAndSignalsItsScalarCache()
    {
        var signals = 0;
        var published = PlaybackTestSession.Signal();
        var observations = new GsmtcPlaybackObservations(new Lock(), () =>
        {
            Interlocked.Increment(ref signals);
            published.TrySetResult();
        });
        var entered = PlaybackTestSession.Signal();
        var release = new TaskCompletionSource<Observation>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var pending = observations.ReadSnapshotAsync(() =>
        {
            entered.TrySetResult();
            return release.Task;
        }, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
            release.TrySetResult(new(MediaPlaybackState.Paused, MediaCapabilities.Play));
            await observations.DrainPendingReadAsync(default);
            await published.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(1, signals);
            Assert.AreEqual(MediaPlaybackState.Paused, observations.Latest.Value.State);
            Assert.IsFalse(observations.IsDirty);
        }
        finally
        {
            release.TrySetResult(new(MediaPlaybackState.Paused, MediaCapabilities.Play));
        }
    }

    [TestMethod]
    public async Task CoalescedEventsAndConcurrentSnapshotAndConfirmationReadersShareOneNativeRead()
    {
        var session = new PlaybackTestSession();
        var previous = await session.SnapshotAsync();
        for (var i = 0; i < 20; i++)
        {
            session.Observations.Invalidate();
        }

        Assert.AreEqual(1, session.Reads);
        var entered = PlaybackTestSession.Signal();
        var release = new TaskCompletionSource<Observation>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.SharedRead = () =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        var snapshots = Enumerable.Range(0, 20).Select(_ => session.SnapshotAsync()).ToArray();
        var confirmation = session.Observations.WaitForObservationAsync(previous.Sequence, session.ReadSharedAsync, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(2, session.Reads);
            release.TrySetResult(new(MediaPlaybackState.Paused, MediaCapabilities.Play));
            var observed = await confirmation;
            foreach (var snapshot in await Task.WhenAll(snapshots))
            {
                Assert.AreEqual(observed, snapshot);
            }

            Assert.AreEqual(2, session.Reads);
            Assert.IsFalse(session.Observations.IsDirty);
        }
        finally
        {
            release.TrySetResult(new(MediaPlaybackState.Paused, MediaCapabilities.Play));
        }
    }

    [TestMethod]
    public async Task EventDuringReadLeavesTheNewerChangeDirty()
    {
        var session = new PlaybackTestSession();
        var entered = PlaybackTestSession.Signal();
        var release = new TaskCompletionSource<Observation>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.SharedRead = () =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        var first = session.SnapshotAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            session.Observations.Invalidate();
            Assert.AreEqual(1, session.Reads);
            release.TrySetResult(session.Value);
            var previous = await first;
            Assert.IsTrue(session.Observations.IsDirty);
            session.SharedRead = null;
            session.Value = new(MediaPlaybackState.Paused, MediaCapabilities.Play);
            var current = await session.SnapshotAsync();
            Assert.IsGreaterThan(previous.ChangeVersion, current.ChangeVersion);
            Assert.IsFalse(session.Observations.IsDirty);
            Assert.AreEqual(2, session.Reads);
            await session.SnapshotAsync();
            Assert.AreEqual(2, session.Reads);
        }
        finally
        {
            release.TrySetResult(session.Value);
        }
    }

    [TestMethod]
    public async Task OlderSnapshotCompletionCannotOverwriteNewerCommandStateOrCapabilities()
    {
        var session = new PlaybackTestSession();
        var entered = PlaybackTestSession.Signal();
        var release = new TaskCompletionSource<Observation>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.SharedRead = () =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        var snapshot = session.SnapshotAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            session.Observations.Invalidate();
            var command = session.Observations.ReadCommand(() => new(MediaPlaybackState.Paused, MediaCapabilities.Play));
            release.TrySetResult(new(MediaPlaybackState.Playing, MediaCapabilities.Pause));
            Assert.AreEqual(command, await snapshot);
            Assert.AreEqual(command, session.Observations.Latest);
            Assert.IsFalse(session.Observations.IsDirty);

            var olderSnapshot = FakeMediaBackend.CreateSnapshot(1, "Player", playbackState: MediaPlaybackState.Playing).Sessions[0];
            var merged = session.Observations.Merge(olderSnapshot, MediaCapabilities.ActivateSource);
            Assert.AreEqual(MediaPlaybackState.Paused, merged.PlaybackState);
            Assert.AreEqual(MediaCapabilities.Play | MediaCapabilities.ActivateSource, merged.Capabilities);
            await session.SnapshotAsync();
            Assert.AreEqual(1, session.Reads);
        }
        finally
        {
            release.TrySetResult(session.Value);
        }
    }

    [TestMethod]
    public async Task FailedReadDoesNotAdvanceSuccessfulVersionOrRepeatItsFailureInLaterSnapshots()
    {
        var session = new PlaybackTestSession();
        var previous = await session.SnapshotAsync();
        session.Observations.Invalidate();
        session.SharedRead = () => throw new InvalidOperationException("Read failed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SnapshotAsync());
        Assert.IsFalse(session.Observations.NeedsSnapshotRead);
        for (var i = 0; i < 5; i++)
        {
            Assert.AreEqual(previous, await session.SnapshotAsync());
        }
        Assert.AreEqual(previous, session.Observations.Latest);
        Assert.IsTrue(session.Observations.IsDirty);
        Assert.AreEqual(2, session.Reads);

        session.SharedRead = null;
        session.Observations.Invalidate();
        Assert.IsTrue(session.Observations.NeedsSnapshotRead);
        await session.SnapshotAsync();
        Assert.IsFalse(session.Observations.IsDirty);
        Assert.AreEqual(3, session.Reads);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ASkippedNewerEventIsReadyWhenATimedOutOlderReadSignalsCompletion(bool failRead)
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        GsmtcPlaybackObservations? observations = null;
        observations = new(new Lock(), TimeSpan.FromMilliseconds(30), TimeSpan.Zero,
            () => ready.TrySetResult(observations!.NeedsSnapshotRead));
        var release = new TaskCompletionSource<Observation>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => observations.ReadSnapshotAsync(() => release.Task, default));
            observations.Invalidate();
            Assert.IsFalse(observations.NeedsSnapshotRead);
            if (failRead)
            {
                release.TrySetException(new InvalidOperationException("Old read failed"));
            }
            else
            {
                release.TrySetResult(new(MediaPlaybackState.Playing, MediaCapabilities.Pause));
            }

            Assert.IsTrue(await ready.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.IsTrue(observations.NeedsSnapshotRead);
            var sample = await observations.ReadSnapshotAsync(
                () => Task.FromResult(new Observation(MediaPlaybackState.Paused, MediaCapabilities.Play)), default);
            Assert.AreEqual(MediaPlaybackState.Paused, sample.Value.State);
            Assert.IsFalse(observations.IsDirty);
        }
        finally
        {
            release.TrySetResult(new(MediaPlaybackState.Paused, MediaCapabilities.Play));
        }
    }

    [TestMethod]
    public async Task EventBetweenSendAndRegistrationIsRetainedEvenBeforeItIsRead()
    {
        var session = new PlaybackTestSession();
        var previous = session.Observations.ReadCommand(() => session.Value);
        session.Value = new(MediaPlaybackState.Paused, MediaCapabilities.Play);
        session.Observations.Invalidate();

        var observed = await session.Observations.WaitForObservationAsync(previous.Sequence, session.ReadSharedAsync, default)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(MediaPlaybackState.Paused, observed.Value.State);
        Assert.AreEqual(1, session.Reads);
    }

    [TestMethod]
    public async Task SnapshotReadStartedBeforeSendCannotConfirmItEvenIfItCompletesAfterSend()
    {
        var observations = new GsmtcPlaybackObservations(new Lock());
        var controller = new GsmtcPlaybackController(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(60));
        var entered = PlaybackTestSession.Signal();
        var release = new TaskCompletionSource<Observation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var sends = 0;
        Task<Observation> Read()
        {
            if (Interlocked.Increment(ref reads) == 2)
            {
                entered.TrySetResult();
                return release.Task;
            }

            return Task.FromResult(new Observation(MediaPlaybackState.Playing, MediaCapabilities.Pause));
        }

        try
        {
            var result = await controller.ExecuteAsync(MediaOperation.Pause, observations, Read, async (markSending, token) =>
            {
                var command = observations.ReadCommand(() => Read().GetAwaiter().GetResult());
                observations.Invalidate();
                var earlier = observations.ReadSnapshotAsync(Read, default);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
                return await GsmtcPlaybackController.SendRevalidatedAsync(MediaOperation.Pause, observations, command, async _ =>
                {
                    sends++;
                    release.TrySetResult(new(MediaPlaybackState.Paused, MediaCapabilities.Play));
                    await earlier;
                    return true;
                }, markSending);
            }, default);
            Assert.AreEqual(MediaBackendCommandStatus.Unconfirmed, result.Status);
            Assert.AreEqual(3, reads);
            Assert.AreEqual(1, sends);
        }
        finally
        {
            release.TrySetResult(new(MediaPlaybackState.Playing, MediaCapabilities.Pause));
        }
    }

    [TestMethod]
    public async Task StoppedConfirmationResetsAcrossAnInterveningPublishedState()
    {
        var session = new PlaybackTestSession();
        session.SharedRead = async () =>
        {
            var read = session.Reads;
            if (read == 3)
            {
                await Task.Delay(60);
            }

            if (read < 4)
            {
                session.Observations.Invalidate();
            }

            return new Observation(read == 3 ? MediaPlaybackState.Changing : MediaPlaybackState.Stopped, MediaCapabilities.Play);
        };

        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await session.ExecuteAsync()).Status);
        Assert.AreEqual(5, session.Reads);
        Assert.HasCount(1, session.Operations);
    }

    private sealed class ObservationLogger : ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<int> Events { get; } = new();
        public TaskCompletionSource Slow { get; } = PlaybackTestSession.Signal();
        public TaskCompletionSource Paused { get; } = PlaybackTestSession.Signal();
        public TaskCompletionSource Resumed { get; } = PlaybackTestSession.Signal();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            this.Events.Enqueue(eventId.Id);
            switch (eventId.Id)
            {
                case 7: this.Slow.TrySetResult(); break;
                case 9: this.Paused.TrySetResult(); break;
                case 10: this.Resumed.TrySetResult(); break;
            }
        }
    }
}