// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Concurrent;
using System.Diagnostics;
using JPSoftworks.MediaControlsExtension.Media.Gsmtc;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Observation = JPSoftworks.MediaControlsExtension.Media.Gsmtc.GsmtcPlaybackController.PlaybackObservation;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class GsmtcPlaybackControllerTests
{
    [TestMethod]
    public async Task EventPublishedBeforeWaiterRegistrationConfirmsWithTwoReadsAndNextSnapshotReusesIt()
    {
        var session = new PlaybackTestSession();
        session.NativeSend = async _ =>
        {
            session.Value = new(MediaPlaybackState.Paused, MediaCapabilities.Play);
            session.Observations.Invalidate();
            await session.SnapshotAsync();
            return true;
        };

        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await session.ExecuteAsync()).Status);
        Assert.AreEqual(2, session.Reads);
        Assert.AreEqual(1, session.CommandReads);
        Assert.HasCount(1, session.Operations);
        var snapshot = await session.SnapshotAsync();
        Assert.AreEqual(MediaPlaybackState.Paused, snapshot.Value.State);
        Assert.AreEqual(2, session.Reads);
        Assert.IsFalse(session.Observations.IsDirty);
    }

    [TestMethod]
    [DataRow(MediaOperation.Play, MediaPlaybackState.Playing, MediaCapabilities.Pause)]
    [DataRow(MediaOperation.Pause, MediaPlaybackState.Paused, MediaCapabilities.Play)]
    public async Task AlreadySatisfiedIntentNeedsOnlyTheGatedRead(
        MediaOperation intent, MediaPlaybackState state, MediaCapabilities capabilities)
    {
        var session = new PlaybackTestSession { Value = new(state, capabilities) };

        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await session.ExecuteAsync(intent)).Status);
        Assert.AreEqual(1, session.Reads);
        Assert.IsEmpty(session.Operations);
        await session.SnapshotAsync();
        Assert.AreEqual(1, session.Reads);
    }

    [TestMethod]
    [DataRow(MediaOperation.Play, MediaPlaybackState.Playing, MediaCapabilities.Play)]
    [DataRow(MediaOperation.Pause, MediaPlaybackState.Paused, MediaCapabilities.Pause)]
    public async Task AlreadyMatchingObservationStillSendsAnEnabledAbsoluteIntent(
        MediaOperation intent, MediaPlaybackState state, MediaCapabilities capabilities)
    {
        var session = new PlaybackTestSession { Value = new(state, capabilities) };

        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await session.ExecuteAsync(intent)).Status);
        CollectionAssert.AreEqual(new[] { intent }, session.Operations.ToArray());
        Assert.AreEqual(2, session.Reads);
    }

    [TestMethod]
    [DataRow(true, MediaBackendCommandStatus.Completed)]
    [DataRow(false, MediaBackendCommandStatus.Unconfirmed)]
    public async Task MissingEventUsesOneFallbackAndNeverResends(bool changeState, MediaBackendCommandStatus expected)
    {
        var session = new PlaybackTestSession
        {
            Controller = new(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(50)),
            RaiseEventOnSend = false,
            ChangeStateOnSend = changeState,
        };

        Assert.AreEqual(expected, (await session.ExecuteAsync()).Status);
        Assert.AreEqual(2, session.Reads);
        Assert.HasCount(1, session.Operations);
        await session.SnapshotAsync();
        Assert.AreEqual(2, session.Reads);
    }

    [TestMethod]
    [DataRow(MediaCapabilities.Play)]
    [DataRow(MediaCapabilities.Play | MediaCapabilities.Pause)]
    public async Task StoppedPauseRequiresTwoPostDecisionReadsSeparatedByFiftyMilliseconds(MediaCapabilities capabilities)
    {
        var session = new PlaybackTestSession
        {
            Value = new(MediaPlaybackState.Stopped, capabilities),
            Controller = new(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(20)),
        };

        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await session.ExecuteAsync()).Status);
        Assert.AreEqual(3, session.Reads);
        Assert.AreEqual(1, session.CommandReads);
        Assert.IsEmpty(session.Operations);
        var times = session.ReadTimes.ToArray();
        Assert.IsTrue(Stopwatch.GetElapsedTime(times[1], times[2]) >= TimeSpan.FromMilliseconds(50));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PauseCanConfirmTwoStoppedObservationsAfterSending(bool raiseEvent)
    {
        var session = new PlaybackTestSession
        {
            RaiseEventOnSend = raiseEvent,
            SentState = MediaPlaybackState.Stopped,
            Controller = new(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100)),
        };

        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await session.ExecuteAsync()).Status);
        Assert.AreEqual(3, session.Reads);
        Assert.HasCount(1, session.Operations);
        var times = session.ReadTimes.ToArray();
        Assert.IsTrue(Stopwatch.GetElapsedTime(times[1], times[2]) >= TimeSpan.FromMilliseconds(50));
    }

    [TestMethod]
    [DataRow(MediaPlaybackState.Unknown)]
    [DataRow(MediaPlaybackState.Changing)]
    [DataRow(MediaPlaybackState.Opened)]
    [DataRow(MediaPlaybackState.Closed)]
    public async Task UnsettledStatesDoNotConfirmPause(MediaPlaybackState state)
    {
        var session = new PlaybackTestSession
        {
            RaiseEventOnSend = false,
            SentState = state,
            Controller = new(TimeSpan.FromMilliseconds(180), TimeSpan.FromMilliseconds(30)),
        };

        Assert.AreEqual(MediaBackendCommandStatus.Unconfirmed, (await session.ExecuteAsync()).Status);
        Assert.AreEqual(2, session.Reads);
        Assert.HasCount(1, session.Operations);
    }

    [TestMethod]
    [DataRow(MediaCapabilities.Stop)]
    [DataRow(MediaCapabilities.Play | MediaCapabilities.Stop)]
    public async Task PlayingStopOnlySourceRequiresExactlyTwoSeparatedObservations(MediaCapabilities capabilities)
    {
        var session = new PlaybackTestSession { Value = new(MediaPlaybackState.Playing, capabilities) };

        Assert.AreEqual(MediaBackendCommandStatus.Unsupported, (await session.ExecuteAsync()).Status);
        Assert.AreEqual(2, session.Reads);
        Assert.AreEqual(1, session.CommandReads);
        Assert.IsEmpty(session.Operations);
        var times = session.ReadTimes.ToArray();
        Assert.IsTrue(Stopwatch.GetElapsedTime(times[0], times[1]) >= TimeSpan.FromMilliseconds(50));
    }

    [TestMethod]
    [DataRow(MediaCapabilities.Pause, MediaOperation.Pause)]
    [DataRow(MediaCapabilities.TogglePlayback, MediaOperation.TogglePlayback)]
    public async Task PauseUsesAControlThatAppearsAfterPlaying(MediaCapabilities capability, MediaOperation expectedOperation)
    {
        var session = new PlaybackTestSession { Value = new(MediaPlaybackState.Playing, MediaCapabilities.Stop) };
        session.SharedRead = () =>
        {
            if (session.Operations.IsEmpty)
            {
                session.Value = session.Value with { Capabilities = MediaCapabilities.Stop | capability };
            }

            return Task.FromResult(session.Value);
        };

        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await session.ExecuteAsync()).Status);
        CollectionAssert.AreEqual(new[] { expectedOperation }, session.Operations.ToArray());
        Assert.AreEqual(4, session.Reads);
    }

    [TestMethod]
    [DataRow(MediaPlaybackState.Changing, MediaCapabilities.Stop)]
    [DataRow(MediaPlaybackState.Playing, MediaCapabilities.None)]
    public async Task StopOnlyConfirmationRequiresConsecutiveObservations(
        MediaPlaybackState interveningState, MediaCapabilities interveningControls)
    {
        var session = new PlaybackTestSession { Value = new(MediaPlaybackState.Playing, MediaCapabilities.Stop) };
        var intervening = PlaybackTestSession.Signal();
        var resume = PlaybackTestSession.Signal();
        session.SharedRead = async () =>
        {
            if (session.Reads == 2)
            {
                session.Value = new(interveningState, interveningControls);
                intervening.TrySetResult();
                await resume.Task;
            }

            return session.Value;
        };
        var transition = session.ExecuteAsync();
        await intervening.Task.WaitAsync(TimeSpan.FromSeconds(2));
        resume.TrySetResult();
        await session.Observations.WithCommandReadAsync(static () => Task.FromResult(true), default);
        Assert.IsFalse(transition.IsCompleted);
        session.Value = new(MediaPlaybackState.Playing, MediaCapabilities.Stop);
        session.Observations.Invalidate();

        Assert.AreEqual(MediaBackendCommandStatus.Unsupported, (await transition).Status);
        Assert.AreEqual(4, session.Reads);
        Assert.IsEmpty(session.Operations);
    }

    [TestMethod]
    [DataRow(MediaOperation.Play, MediaPlaybackState.Paused)]
    [DataRow(MediaOperation.Pause, MediaPlaybackState.Playing)]
    [DataRow(MediaOperation.Pause, MediaPlaybackState.Changing)]
    public async Task DisabledDirectionWaitsForAnEventAndRevalidatesReadiness(MediaOperation intent, MediaPlaybackState initialState)
    {
        var session = new PlaybackTestSession { Value = new(initialState, MediaCapabilities.None) };
        var transition = session.ExecuteAsync(intent);
        await session.Revalidated.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsEmpty(session.Operations);
        session.Value = session.Value with { Capabilities = intent == MediaOperation.Play ? MediaCapabilities.Play : MediaCapabilities.Pause };
        session.Observations.Invalidate();

        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await transition).Status);
        CollectionAssert.AreEqual(new[] { intent }, session.Operations.ToArray());
        Assert.AreEqual(2, session.CommandReads);
    }

    [TestMethod]
    public async Task TransientEventReadFailureCanConfirmThroughTheUnusedFallback()
    {
        var session = new PlaybackTestSession { Controller = new(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(30)) };
        session.SharedRead = () => session.Reads == 2
            ? throw new InvalidOperationException("Transient event read failure") : Task.FromResult(session.Value);

        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await session.ExecuteAsync()).Status);
        Assert.AreEqual(3, session.Reads);
        Assert.HasCount(1, session.Operations);
        Assert.IsFalse(session.Observations.IsDirty);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StaleOrRetiredEventReadDoesNotUseTheFallback(bool retired)
    {
        var session = new PlaybackTestSession();
        session.SharedRead = () => throw (retired ? new GsmtcSessionRetiredException() : new ObjectDisposedException("Stale session"));
        if (retired)
        {
            Assert.AreEqual(MediaBackendCommandStatus.SessionGone, (await session.ExecuteAsync()).Status);
        }
        else
        {
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => session.ExecuteAsync());
        }

        Assert.AreEqual(2, session.Reads);
        Assert.HasCount(1, session.Operations);
    }

    [TestMethod]
    public async Task ReadFailureAfterSendingUsesTheFallbackOnceWithoutAdvancingTheSuccessfulWatermark()
    {
        var session = new PlaybackTestSession();
        session.SharedRead = () => throw new InvalidOperationException("Observation failed");

        Assert.AreEqual(MediaBackendCommandStatus.Unconfirmed, (await session.ExecuteAsync()).Status);
        Assert.AreEqual(3, session.Reads);
        Assert.HasCount(1, session.Operations);
        Assert.IsTrue(session.Observations.IsDirty);
        Assert.AreEqual(1L, session.Observations.Latest.Sequence);
    }

    [TestMethod]
    public async Task CancellationAfterMutationCannotBecomeSuccessOrReplay()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new PlaybackTestSession();
        session.NativeSend = async _ =>
        {
            session.Value = new(MediaPlaybackState.Paused, MediaCapabilities.Play);
            session.Observations.Invalidate();
            await session.SnapshotAsync();
            cancellation.Cancel();
            return true;
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => session.ExecuteAsync(cancellationToken: cancellation.Token));
        Assert.HasCount(1, session.Operations);
        Assert.AreEqual(2, session.Reads);
    }

    [TestMethod]
    public async Task RetirementWhileWaitingReturnsSessionGoneAndAReplacementCannotConfirmIt()
    {
        var session = new PlaybackTestSession { RaiseEventOnSend = false, ChangeStateOnSend = false };
        var transition = session.ExecuteAsync();
        await session.Sent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        session.Observations.Retire();
        var replacement = new PlaybackTestSession { Value = new(MediaPlaybackState.Paused, MediaCapabilities.Play) };
        await replacement.SnapshotAsync();
        session.Observations.Invalidate();

        Assert.AreEqual(MediaBackendCommandStatus.SessionGone, (await transition).Status);
        Assert.HasCount(1, session.Operations);
        Assert.AreEqual(1, session.Reads);
        await Assert.ThrowsAsync<GsmtcSessionRetiredException>(() => session.SnapshotAsync());
    }

    [TestMethod]
    public async Task NativeRejectionIsFailedAndNeverRetried()
    {
        var session = new PlaybackTestSession { NativeSend = _ => Task.FromResult(false) };

        Assert.AreEqual(MediaBackendCommandStatus.Failed, (await session.ExecuteAsync()).Status);
        Assert.AreEqual(1, session.Reads);
        Assert.HasCount(1, session.Operations);
    }

    [TestMethod]
    public async Task AlreadySatisfiedIntentStillProcessesAncillaryPausesOnce()
    {
        var pausedSessions = 0;
        var session = new PlaybackTestSession { Value = new(MediaPlaybackState.Playing, MediaCapabilities.Pause) };
        session.BeforeRevalidation = (markSending, _) =>
        {
            markSending();
            pausedSessions += 2;
            return Task.CompletedTask;
        };

        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await session.ExecuteAsync(MediaOperation.Play)).Status);
        Assert.AreEqual(2, pausedSessions);
        Assert.AreEqual(1, session.Reads);
        Assert.IsEmpty(session.Operations);
    }

    [TestMethod]
    public async Task AncillaryMutationDoesNotReplayWhenPrimaryIsTemporarilyNotReady()
    {
        var pausedSessions = 0;
        var session = new PlaybackTestSession { Value = new(MediaPlaybackState.Paused, MediaCapabilities.None) };
        session.BeforeRevalidation = (markSending, _) =>
        {
            markSending();
            pausedSessions++;
            return Task.CompletedTask;
        };

        Assert.AreEqual(MediaBackendCommandStatus.Unconfirmed, (await session.ExecuteAsync(MediaOperation.Play)).Status);
        Assert.AreEqual(1, pausedSessions);
        Assert.AreEqual(1, session.Reads);
        Assert.IsEmpty(session.Operations);
    }

    [TestMethod]
    public async Task UnsentStoppedIntentReturnsToReadinessWhenPlaybackChanges()
    {
        var session = new PlaybackTestSession
        {
            Value = new(MediaPlaybackState.Stopped, MediaCapabilities.None),
        };
        var transition = session.ExecuteAsync();
        await session.Revalidated.Task.WaitAsync(TimeSpan.FromSeconds(2));
        session.Value = new(MediaPlaybackState.Playing, MediaCapabilities.Pause);
        session.Observations.Invalidate();

        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await transition).Status);
        CollectionAssert.AreEqual(new[] { MediaOperation.Pause }, session.Operations.ToArray());
        Assert.AreEqual(4, session.Reads);
    }

    [TestMethod]
    public async Task StoppedFallbackThatChangesAgainCannotStartAnUnboundedReadinessCycle()
    {
        var session = new PlaybackTestSession
        {
            Value = new(MediaPlaybackState.Stopped, MediaCapabilities.None),
            Controller = new(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(30)),
        };
        session.SharedRead = () => Task.FromResult(session.Reads == 2
            ? session.Value : new Observation(MediaPlaybackState.Playing, MediaCapabilities.Pause));

        Assert.AreEqual(MediaBackendCommandStatus.Unavailable, (await session.ExecuteAsync()).Status);
        Assert.AreEqual(3, session.Reads);
        Assert.IsEmpty(session.Operations);
    }

    [TestMethod]
    public async Task ConcurrentInputCannotStartAnotherTransitionOnTheSameBinding()
    {
        var session = new PlaybackTestSession { ChangeStateOnSend = false, RaiseEventOnSend = false };
        using var cancellation = new CancellationTokenSource();
        var first = session.ExecuteAsync(cancellationToken: cancellation.Token);
        await session.Sent.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(MediaBackendCommandStatus.Unavailable, (await session.ExecuteAsync(MediaOperation.Play)).Status);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => first);
        Assert.HasCount(1, session.Operations);
    }
}

internal sealed class PlaybackTestSession
{
    public GsmtcPlaybackObservations Observations { get; init; } = new(new Lock());
    public GsmtcControlGate Controls { get; init; } = new(NullLogger.Instance);
    public GsmtcPlaybackController Controller { get; set; } = new();
    public Observation Value { get; set; } = new(MediaPlaybackState.Playing, MediaCapabilities.Pause);
    public Func<Task<Observation>>? SharedRead { get; set; }
    public Func<Observation>? CommandRead { get; set; }
    public Func<MediaOperation, Task<bool>>? NativeSend { get; set; }
    public Func<Action, CancellationToken, Task>? BeforeRevalidation { get; set; }
    public bool RaiseEventOnSend { get; set; } = true;
    public bool ChangeStateOnSend { get; set; } = true;
    public MediaPlaybackState? SentState { get; set; }
    public ConcurrentQueue<MediaOperation> Operations { get; } = new();
    public ConcurrentQueue<long> ReadTimes { get; } = new();
    public TaskCompletionSource Sent { get; } = Signal();
    public TaskCompletionSource Revalidated { get; } = Signal();
    public int Reads;
    public int CommandReads;

    public Task<MediaBackendCommandResult> ExecuteAsync(
        MediaOperation intent = MediaOperation.Pause, CancellationToken cancellationToken = default) =>
        this.Controller.ExecuteAsync(intent, this.Observations, this.ReadSharedAsync,
            (markSending, token) => this.Controls.RunCommandAsync(async () =>
            {
                if (this.BeforeRevalidation is { } before)
                {
                    await before(markSending, token);
                }

                token.ThrowIfCancellationRequested();
                var sample = this.Observations.ReadCommand(() =>
                {
                    Interlocked.Increment(ref this.CommandReads);
                    this.CountRead();
                    return this.CommandRead?.Invoke() ?? this.Value;
                });
                var result = await GsmtcPlaybackController.SendRevalidatedAsync(intent, this.Observations, sample, async operation =>
                {
                    this.Operations.Enqueue(operation);
                    this.Sent.TrySetResult();
                    if (this.NativeSend is { } send)
                    {
                        return await send(operation);
                    }

                    if (this.ChangeStateOnSend)
                    {
                        this.Value = new(this.SentState ?? (intent == MediaOperation.Play ? MediaPlaybackState.Playing : MediaPlaybackState.Paused),
                            intent == MediaOperation.Play ? MediaCapabilities.Pause : MediaCapabilities.Play);
                    }

                    if (this.RaiseEventOnSend)
                    {
                        this.Observations.Invalidate();
                    }

                    return true;
                }, markSending);
                this.Revalidated.TrySetResult();
                return result;
            }, "Playback", token), cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

    public Task<GsmtcPlaybackObservations.Sample> SnapshotAsync(CancellationToken cancellationToken = default) =>
        this.Observations.ReadSnapshotAsync(this.ReadSharedAsync, cancellationToken);

    public Task<Observation> ReadSharedAsync()
    {
        this.CountRead();
        return this.SharedRead?.Invoke() ?? Task.FromResult(this.Value);
    }

    private void CountRead()
    {
        Interlocked.Increment(ref this.Reads);
        this.ReadTimes.Enqueue(Stopwatch.GetTimestamp());
    }

    public static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}