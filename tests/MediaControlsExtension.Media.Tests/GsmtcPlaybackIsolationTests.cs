// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Gsmtc;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Observation = JPSoftworks.MediaControlsExtension.Media.Gsmtc.GsmtcPlaybackController.PlaybackObservation;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class GsmtcPlaybackIsolationTests
{
    [TestMethod]
    public async Task ReadinessFallbackDrainsAnEventReadBeforeGatedRevalidation()
    {
        var session = new PlaybackTestSession
        {
            Value = new(MediaPlaybackState.Paused, MediaCapabilities.None),
            Controller = new(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100)),
        };
        var entered = PlaybackTestSession.Signal();
        var release = new TaskCompletionSource<Observation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeReads = 0;
        session.SharedRead = async () =>
        {
            Interlocked.Increment(ref activeReads);
            entered.TrySetResult();
            try
            {
                return await release.Task;
            }
            finally
            {
                Interlocked.Decrement(ref activeReads);
            }
        };
        session.CommandRead = () =>
        {
            Assert.AreEqual(0, Volatile.Read(ref activeReads));
            return session.Value;
        };
        var transition = session.ExecuteAsync(MediaOperation.Play);
        try
        {
            await session.Revalidated.Task.WaitAsync(TimeSpan.FromSeconds(2));
            session.Observations.Invalidate();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(200);
            Assert.AreEqual(1, session.CommandReads);
            Assert.IsFalse(transition.IsCompleted);
            session.SharedRead = null;
            session.Value = new(MediaPlaybackState.Paused, MediaCapabilities.Play);
            release.TrySetResult(session.Value);

            Assert.AreEqual(MediaBackendCommandStatus.Completed, (await transition).Status);
            Assert.AreEqual(2, session.CommandReads);
            Assert.HasCount(1, session.Operations);
        }
        finally
        {
            release.TrySetResult(session.Value);
        }
    }

    [TestMethod]
    public async Task CommandReservationPreventsSnapshotReadsWhileWaitingForTheControlGate()
    {
        var observations = new GsmtcPlaybackObservations(new Lock());
        var previous = observations.ReadCommand(() => new(MediaPlaybackState.Playing, MediaCapabilities.Pause));
        observations.Invalidate();
        var controls = new GsmtcControlGate(NullLogger.Instance);
        var entered = PlaybackTestSession.Signal();
        var release = PlaybackTestSession.Signal();
        var blocker = controls.RunCommandAsync(async () =>
        {
            entered.TrySetResult();
            await release.Task;
            return true;
        }, "Other session", default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var command = observations.WithCommandReadAsync(() => controls.RunCommandAsync(
                () => Task.FromResult(observations.ReadCommand(() => new(MediaPlaybackState.Paused, MediaCapabilities.Play))),
                "Revalidation", default), default);
            Assert.AreEqual(previous, await observations.ReadSnapshotAsync(
                () => throw new AssertFailedException("A reserved command must prevent a snapshot read."), default));
            Assert.IsFalse(command.IsCompleted);
            release.TrySetResult();
            await blocker;
            Assert.AreEqual(MediaPlaybackState.Paused, (await command).Value.State);
            Assert.IsFalse(observations.NeedsSnapshotRead);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HungPlaybackReadIsBoundedPerBindingAndDoesNotBlockAnotherSession(bool hangDuringConfirmation)
    {
        var controls = new GsmtcControlGate(NullLogger.Instance);
        var stalled = new PlaybackTestSession
        {
            Controls = controls,
            Controller = new(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(30)),
        };
        var entered = PlaybackTestSession.Signal();
        var release = new TaskCompletionSource<Observation>(TaskCreationOptions.RunContinuationsAsynchronously);
        stalled.SharedRead = () =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        Task<GsmtcPlaybackObservations.Sample>? snapshot = null;
        try
        {
            if (!hangDuringConfirmation)
            {
                snapshot = stalled.SnapshotAsync();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }

            var first = await stalled.ExecuteAsync();
            Assert.AreEqual(hangDuringConfirmation ? MediaBackendCommandStatus.Unconfirmed : MediaBackendCommandStatus.Unavailable,
                first.Status);

            var healthy = new PlaybackTestSession { Controls = controls };
            Assert.AreEqual(MediaBackendCommandStatus.Completed, (await healthy.ExecuteAsync()).Status);
            Assert.IsFalse(release.Task.IsCompleted);

            Assert.AreEqual(MediaBackendCommandStatus.Unavailable, (await stalled.ExecuteAsync()).Status);
            Assert.AreEqual(hangDuringConfirmation ? 2 : 1, stalled.Reads);
            Assert.HasCount(hangDuringConfirmation ? 1 : 0, stalled.Operations);
            Assert.IsFalse(controls.IsCircuitOpen);

            stalled.Value = new(MediaPlaybackState.Paused, MediaCapabilities.Play);
            release.TrySetResult(stalled.Value);
            await stalled.Observations.WithCommandReadAsync(static () => Task.FromResult(true), default).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(MediaBackendCommandStatus.Completed, (await stalled.ExecuteAsync()).Status);
        }
        finally
        {
            release.TrySetResult(new(MediaPlaybackState.Paused, MediaCapabilities.Play));
            if (snapshot is not null)
            {
                await snapshot;
            }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedSnapshotReadDrainedBeforeTheCommandDoesNotFailTheCommand(bool staleFailure)
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
            var transition = session.ExecuteAsync();
            session.SharedRead = null;
            release.TrySetException(staleFailure
                ? new ObjectDisposedException("Stale snapshot")
                : new InvalidOperationException("Snapshot read failed"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => snapshot);

            Assert.AreEqual(MediaBackendCommandStatus.Completed, (await transition).Status);
            Assert.AreEqual(1, session.CommandReads);
            Assert.AreEqual(3, session.Reads);
            Assert.HasCount(1, session.Operations);
        }
        finally
        {
            release.TrySetResult(session.Value);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DrainingASnapshotStillStopsOnCancellationOrRetirement(bool retire)
    {
        var session = new PlaybackTestSession();
        var entered = PlaybackTestSession.Signal();
        var release = new TaskCompletionSource<Observation>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.SharedRead = () =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        using var cancellation = new CancellationTokenSource();
        var snapshot = session.SnapshotAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var command = session.ExecuteAsync(cancellationToken: cancellation.Token);
            if (retire)
            {
                session.Observations.Retire();
                Assert.AreEqual(MediaBackendCommandStatus.SessionGone, (await command).Status);
            }
            else
            {
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => command);
            }

            Assert.AreEqual(0, session.CommandReads);
            Assert.IsEmpty(session.Operations);
        }
        finally
        {
            release.TrySetResult(session.Value);
            await ((Task)snapshot).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SnapshotWaitForAHungPlaybackReadReleasesTheGlobalGate(bool startedByConfirmation)
    {
        var signals = 0;
        var published = PlaybackTestSession.Signal();
        var observations = new GsmtcPlaybackObservations(new Lock(), TimeSpan.FromMilliseconds(30), TimeSpan.Zero, () =>
        {
            if (Interlocked.Increment(ref signals) == 2)
            {
                published.TrySetResult();
            }
        });
        var gate = new GsmtcObservationGate(NullLogger.Instance, TimeSpan.FromSeconds(2), TimeSpan.Zero);
        var previous = observations.ReadCommand(() => new(MediaPlaybackState.Playing, MediaCapabilities.Pause));
        observations.Invalidate();
        var entered = PlaybackTestSession.Signal();
        var release = new TaskCompletionSource<Observation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        Task<Observation> Read()
        {
            Interlocked.Increment(ref reads);
            entered.TrySetResult();
            return release.Task;
        }

        var confirmation = startedByConfirmation
            ? observations.WaitForObservationAsync(previous.Sequence, Read, default) : null;
        var snapshot = gate.RunAsync(() => observations.ReadSnapshotAsync(Read, default), "Snapshot playback", default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAsync<TimeoutException>(() => snapshot.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.IsFalse(release.Task.IsCompleted);
            Assert.IsFalse(gate.IsBlocked);
            Assert.IsFalse(observations.NeedsSnapshotRead);
            Assert.IsTrue(observations.IsDirty);
            Assert.IsTrue(await gate.RunAsync(() => Task.FromResult(true), "Other session artwork", default)
                .WaitAsync(TimeSpan.FromSeconds(1)));
            for (var i = 0; i < 5; i++)
            {
                Assert.AreEqual(previous, await observations.ReadSnapshotAsync(Read, default));
            }

            Assert.AreEqual(1, reads);
            release.TrySetResult(new(MediaPlaybackState.Paused, MediaCapabilities.Play));
            await published.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (confirmation is not null)
            {
                Assert.AreEqual(MediaPlaybackState.Paused, (await confirmation).Value.State);
            }

            Assert.AreEqual(MediaPlaybackState.Paused, observations.Latest.Value.State);
            Assert.AreEqual(2, signals);
        }
        finally
        {
            release.TrySetResult(new(MediaPlaybackState.Paused, MediaCapabilities.Play));
        }
    }

    [TestMethod]
    public async Task ConfirmationDoesNotQueueBehindABusyNativeCommand()
    {
        var session = new PlaybackTestSession();
        var confirming = PlaybackTestSession.Signal();
        var allowConfirmation = new TaskCompletionSource<Observation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherStarted = PlaybackTestSession.Signal();
        var releaseOther = PlaybackTestSession.Signal();
        session.SharedRead = () =>
        {
            confirming.TrySetResult();
            return allowConfirmation.Task;
        };
        Task<bool>? other = null;
        var transition = session.ExecuteAsync();
        try
        {
            await confirming.Task.WaitAsync(TimeSpan.FromSeconds(2));
            other = session.Controls.RunCommandAsync(async () =>
            {
                otherStarted.TrySetResult();
                await releaseOther.Task;
                return true;
            }, "Other session", default);
            await otherStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            allowConfirmation.TrySetResult(new(MediaPlaybackState.Paused, MediaCapabilities.Play));

            Assert.AreEqual(MediaBackendCommandStatus.Completed, (await transition).Status);
            Assert.AreEqual(2, session.Reads);
            Assert.IsFalse(other.IsCompleted);
        }
        finally
        {
            allowConfirmation.TrySetResult(new(MediaPlaybackState.Paused, MediaCapabilities.Play));
            releaseOther.TrySetResult();
            if (other is not null)
            {
                await other.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
    }

    [TestMethod]
    public async Task TimedOutReadRecoversWithoutOpeningTheControlCircuit()
    {
        var observations = new GsmtcObservationGate(NullLogger.Instance, TimeSpan.FromMilliseconds(60), TimeSpan.Zero);
        var release = new TaskCompletionSource<Observation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new PlaybackTestSession();
        session.SharedRead = () => observations.RunAsync(() => release.Task, "Playback", default);
        try
        {
            Assert.AreEqual(MediaBackendCommandStatus.Unconfirmed, (await session.ExecuteAsync()).Status);
            Assert.IsTrue(observations.IsBlocked);
            Assert.AreEqual(3, session.Reads);
            Assert.IsTrue(await session.Controls.RunCommandAsync(() => Task.FromResult(true), "SkipNext", default));
            Assert.IsFalse(session.Controls.IsCircuitOpen);
            release.TrySetResult(new(MediaPlaybackState.Paused, MediaCapabilities.Play));
            session.SharedRead = null;
            Assert.AreEqual(MediaBackendCommandStatus.Completed, (await session.ExecuteAsync()).Status);
        }
        finally
        {
            release.TrySetResult(new(MediaPlaybackState.Paused, MediaCapabilities.Play));
        }
    }

    [TestMethod]
    public async Task RetirementWakesWaitersEvenWhileTheSharedNativeReadIsHung()
    {
        var session = new PlaybackTestSession();
        var entered = PlaybackTestSession.Signal();
        var release = new TaskCompletionSource<Observation>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.SharedRead = () =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        var transition = session.ExecuteAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            session.Observations.Retire();

            Assert.AreEqual(MediaBackendCommandStatus.SessionGone, (await transition).Status);
            Assert.IsFalse(release.Task.IsCompleted);
            Assert.AreEqual(2, session.Reads);
        }
        finally
        {
            release.TrySetResult(new(MediaPlaybackState.Paused, MediaCapabilities.Play));
        }
    }
}