// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

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
    [DataRow(MediaOperation.Pause, MediaPlaybackState.Stopped)]
    [DataRow(MediaOperation.Pause, MediaPlaybackState.Paused)]
    [DataRow(MediaOperation.Play, MediaPlaybackState.Playing)]
    public async Task UnsentIntentReturnsToReadinessWhenPlaybackChanges(MediaOperation intent, MediaPlaybackState initialState)
    {
        var controls = new GsmtcControlGate(NullLogger.Instance);
        var desired = intent == MediaOperation.Play ? MediaPlaybackState.Playing : MediaPlaybackState.Paused;
        var opposite = intent == MediaOperation.Play ? MediaPlaybackState.Paused : MediaPlaybackState.Playing;
        var capability = intent == MediaOperation.Play ? MediaCapabilities.Play : MediaCapabilities.Pause;
        var observed = new Observation(initialState, MediaCapabilities.None);
        var revalidated = false;
        var confirmationsAfterSend = 0;
        var operations = new List<MediaOperation>();
        var controller = new GsmtcPlaybackController(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(10));
        var result = await controller.ExecuteAsync(intent,
            _ =>
            {
                if (revalidated)
                {
                    observed = new(operations.Count > 0 && ++confirmationsAfterSend > 1 ? desired : opposite, capability);
                }

                return Task.FromResult(observed);
            },
            (_, markSending, token) => controls.RunCommandAsync(async () =>
            {
                var accepted = await GsmtcPlaybackController.SendRevalidatedAsync(intent, observed, operation =>
                {
                    operations.Add(operation);
                    return Task.FromResult(true);
                }, markSending);
                revalidated = true;
                return accepted;
            }, "Playback", token), default).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(MediaBackendCommandStatus.Completed, result.Status);
        CollectionAssert.AreEqual(new[] { intent }, operations);
        Assert.AreEqual(desired, observed.State);
    }

    [TestMethod]
    public async Task UnsentRetriesResetStoppedConfirmationAndKeepTheOriginalDeadline()
    {
        var controls = new GsmtcControlGate(NullLogger.Instance);
        var reads = 0;
        var revalidations = 0;
        var observed = new Observation(MediaPlaybackState.Stopped, MediaCapabilities.None);
        var controller = new GsmtcPlaybackController(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(10));
        var result = await controller.ExecuteAsync(MediaOperation.Pause,
            _ =>
            {
                observed = new(++reads % 3 == 0 ? MediaPlaybackState.Playing : MediaPlaybackState.Stopped, MediaCapabilities.None);
                return Task.FromResult(observed);
            },
            (_, markSending, token) => controls.RunCommandAsync(() =>
            {
                revalidations++;
                return GsmtcPlaybackController.SendRevalidatedAsync(MediaOperation.Pause, observed,
                    _ => throw new AssertFailedException("No usable Pause control was observed."), markSending);
            }, "Pause", token), default).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(MediaBackendCommandStatus.Unavailable, result.Status);
        Assert.IsGreaterThan(1, revalidations);
    }

    [TestMethod]
    [DataRow(MediaCapabilities.Play)]
    [DataRow(MediaCapabilities.Play | MediaCapabilities.Pause)]
    public async Task StoppedPauseConfirmsWithoutSendingOutsideTheControlGate(MediaCapabilities capabilities)
    {
        var controls = new GsmtcControlGate(NullLogger.Instance);
        var timestamps = new List<long>();
        var rechecks = 0;
        var controller = new GsmtcPlaybackController(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(50));
        var result = await controller.ExecuteAsync(MediaOperation.Pause,
            async token =>
            {
                timestamps.Add(Stopwatch.GetTimestamp());
                Assert.IsTrue(await controls.RunCommandAsync(() => Task.FromResult(true), "Independent", token));
                return new Observation(MediaPlaybackState.Stopped, capabilities);
            },
            (operation, markSending, token) => controls.RunCommandAsync(() =>
            {
                Assert.IsNull(operation);
                Interlocked.Increment(ref rechecks);
                return GsmtcPlaybackController.SendRevalidatedAsync(MediaOperation.Pause,
                    new(MediaPlaybackState.Stopped, capabilities),
                    _ => throw new AssertFailedException("A stopped source must not receive Pause."), markSending);
            }, "Pause", token), default).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(MediaBackendCommandStatus.Completed, result.Status);
        Assert.AreEqual(1, rechecks);
        Assert.HasCount(3, timestamps);
        Assert.IsTrue(Stopwatch.GetElapsedTime(timestamps[1], timestamps[2]) >= TimeSpan.FromMilliseconds(40));
    }

    [TestMethod]
    [DataRow(MediaPlaybackState.Stopped, MediaBackendCommandStatus.Completed)]
    [DataRow(MediaPlaybackState.Unknown, MediaBackendCommandStatus.Unconfirmed)]
    [DataRow(MediaPlaybackState.Changing, MediaBackendCommandStatus.Unconfirmed)]
    [DataRow(MediaPlaybackState.Opened, MediaBackendCommandStatus.Unconfirmed)]
    [DataRow(MediaPlaybackState.Closed, MediaBackendCommandStatus.Unconfirmed)]
    public async Task PauseConfirmationAcceptsStoppedButNotUnsettledStates(
        MediaPlaybackState observedState, MediaBackendCommandStatus expected)
    {
        var sends = 0;
        var controller = new GsmtcPlaybackController(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(10));
        var result = await controller.ExecuteAsync(MediaOperation.Pause,
            _ => Task.FromResult(new Observation(sends == 0 ? MediaPlaybackState.Playing : observedState, MediaCapabilities.Pause)),
            (_, markSending, _) =>
            {
                markSending();
                Interlocked.Increment(ref sends);
                return Task.FromResult(true);
            }, default).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(expected, result.Status);
        Assert.AreEqual(1, sends);
    }

    [TestMethod]
    public async Task StoppedConfirmationRequiresConsecutiveObservations()
    {
        var sent = false;
        var confirmations = 0;
        var result = await CreateController().ExecuteAsync(MediaOperation.Pause,
            _ => Task.FromResult(new Observation(!sent ? MediaPlaybackState.Playing :
                ++confirmations == 2 ? MediaPlaybackState.Changing : MediaPlaybackState.Stopped, MediaCapabilities.Pause)),
            (_, markSending, _) =>
            {
                markSending();
                sent = true;
                return Task.FromResult(true);
            }, default).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(MediaBackendCommandStatus.Completed, result.Status);
        Assert.AreEqual(4, confirmations);
    }

    [TestMethod]
    public async Task MissingEventIsRecoveredByReadsWhileOtherNativeCommandsProceed()
    {
        var controller = CreateController();
        var gate = new GsmtcControlGate(NullLogger.Instance);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = (int)MediaPlaybackState.Playing;
        var nativeCalls = 0;
        var transition = controller.ExecuteAsync(MediaOperation.Pause,
            _ => Task.FromResult(Observe((MediaPlaybackState)Volatile.Read(ref state))),
            (operation, markSending, token) => gate.RunCommandAsync(() =>
            {
                markSending();
                Assert.AreEqual(MediaOperation.Pause, operation);
                Interlocked.Increment(ref nativeCalls);
                sent.TrySetResult();
                return Task.FromResult(true);
            }, "Pause", token), default);

        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(transition.IsCompleted);
        Assert.IsTrue(await gate.RunCommandAsync(() => Task.FromResult(true), "Independent", default)
            .WaitAsync(TimeSpan.FromSeconds(1)));
        Volatile.Write(ref state, (int)MediaPlaybackState.Paused);
        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await transition.WaitAsync(TimeSpan.FromSeconds(2))).Status);
        Assert.AreEqual(1, nativeCalls);
    }

    [TestMethod]
    public async Task RepeatedNonmatchingReadsCannotExtendTheDeadlineOrResend()
    {
        var reads = 0;
        var sends = 0;
        var controller = new GsmtcPlaybackController(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(10));
        var result = await controller.ExecuteAsync(MediaOperation.Pause,
            _ =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult(Observe(MediaPlaybackState.Playing));
            },
            (_, markSending, _) =>
            {
                markSending();
                Interlocked.Increment(ref sends);
                return Task.FromResult(true);
            }, default).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(MediaBackendCommandStatus.Unconfirmed, result.Status);
        Assert.IsGreaterThan(1, reads);
        Assert.AreEqual(1, sends);
    }

    [TestMethod]
    public async Task HungConfirmationReadKeepsOnlyThePlaybackObservationGateOccupied()
    {
        var controller = new GsmtcPlaybackController(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(10));
        var gate = new GsmtcObservationGate(NullLogger.Instance);
        var controls = new GsmtcControlGate(NullLogger.Instance);
        var release = new TaskCompletionSource<Observation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = false;
        var reads = 0;
        var nativeReads = 0;
        Task<Observation> ReadAsync(CancellationToken token) => gate.RunAsync(() =>
        {
            Interlocked.Increment(ref nativeReads);
            return sent ? release.Task : Task.FromResult(Observe(MediaPlaybackState.Playing));
        }, "Playback", token);

        try
        {
            var result = await controller.ExecuteAsync(MediaOperation.Pause, token =>
                {
                    Interlocked.Increment(ref reads);
                    return ReadAsync(token);
                }, (_, markSending, _) =>
                {
                    markSending();
                    sent = true;
                    return Task.FromResult(true);
                }, default).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.AreEqual(MediaBackendCommandStatus.Unconfirmed, result.Status);
            Assert.IsTrue(await controls.RunCommandAsync(() => Task.FromResult(true), "SkipNext", default)
                .WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.IsFalse(controls.IsCircuitOpen);
            var blocked = await controller.ExecuteAsync(MediaOperation.Play, ReadAsync,
                (_, _, _) => throw new AssertFailedException("A hung observation must prevent another send."), default)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(MediaBackendCommandStatus.Unavailable, blocked.Status);
            Assert.AreEqual(2, reads);
            Assert.AreEqual(2, nativeReads);
        }
        finally
        {
            release.TrySetResult(Observe(MediaPlaybackState.Paused));
        }
    }

    [TestMethod]
    public async Task ReadFailureAfterAcknowledgmentIsUnconfirmed()
    {
        var sent = false;
        var result = await CreateController().ExecuteAsync(MediaOperation.Pause,
            _ => sent ? throw new InvalidOperationException("Observation failed") : Task.FromResult(Observe(MediaPlaybackState.Playing)),
            (_, markSending, _) => { markSending(); sent = true; return Task.FromResult(true); }, default);
        Assert.AreEqual(MediaBackendCommandStatus.Unconfirmed, result.Status);
    }

    [TestMethod]
    public async Task RetirementAndCancellationDoNotTurnIntoSuccessfulConfirmation()
    {
        using var cancellation = new CancellationTokenSource();
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = CreateController();
        var transition = controller.ExecuteAsync(MediaOperation.Pause,
            _ => Task.FromResult(Observe(MediaPlaybackState.Playing)),
            (_, markSending, _) => { markSending(); sent.TrySetResult(); return Task.FromResult(true); }, cancellation.Token);
        await sent.Task;
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => transition);

        var retiring = false;
        await Assert.ThrowsAsync<GsmtcSessionRetiredException>(() => controller.ExecuteAsync(MediaOperation.Pause,
            _ => retiring ? throw new GsmtcSessionRetiredException() : Task.FromResult(Observe(MediaPlaybackState.Playing)),
            (_, markSending, _) => { markSending(); retiring = true; return Task.FromResult(true); }, default));
    }

    [TestMethod]
    [DataRow(MediaCapabilities.Stop)]
    [DataRow(MediaCapabilities.Play | MediaCapabilities.Stop)]
    public async Task PlayingStopOnlySourceRequiresTwoSeparatedObservations(MediaCapabilities capabilities)
    {
        var timestamps = new List<long>();
        var result = await new GsmtcPlaybackController().ExecuteAsync(MediaOperation.Pause,
            _ =>
            {
                timestamps.Add(Stopwatch.GetTimestamp());
                Assert.IsTrue(timestamps.Count <= 2, "Two stop-only observations should finish readiness.");
                return Task.FromResult(new Observation(MediaPlaybackState.Playing, capabilities));
            },
            (_, _, _) => throw new AssertFailedException("Unsupported Pause must not send a native operation."), default)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(MediaBackendCommandStatus.Unsupported, result.Status);
        Assert.HasCount(2, timestamps);
        Assert.IsTrue(Stopwatch.GetElapsedTime(timestamps[0], timestamps[1]) >= TimeSpan.FromMilliseconds(40),
            "The default 50 ms poll must separate the reads, allowing for timer resolution.");
    }

    [TestMethod]
    [DataRow(MediaCapabilities.Pause, MediaOperation.Pause)]
    [DataRow(MediaCapabilities.TogglePlayback, MediaOperation.TogglePlayback)]
    public async Task PauseUsesAControlThatAppearsAfterPlaying(MediaCapabilities capability, MediaOperation expectedOperation)
    {
        var reads = 0;
        var sent = false;
        var operations = new List<MediaOperation>();
        var result = await CreateController().ExecuteAsync(MediaOperation.Pause,
            _ => Task.FromResult(new Observation(sent ? MediaPlaybackState.Paused : MediaPlaybackState.Playing,
                Interlocked.Increment(ref reads) == 1 ? MediaCapabilities.Stop : MediaCapabilities.Stop | capability)),
            (_, markSending, _) => GsmtcPlaybackController.SendRevalidatedAsync(MediaOperation.Pause,
                new(MediaPlaybackState.Playing, MediaCapabilities.Stop | capability), operation =>
                {
                    operations.Add(operation);
                    sent = true;
                    return Task.FromResult(true);
                }, markSending), default);

        Assert.AreEqual(MediaBackendCommandStatus.Completed, result.Status);
        CollectionAssert.AreEqual(new[] { expectedOperation }, operations);
        Assert.AreEqual(3, reads);
    }

    [TestMethod]
    [DataRow(MediaPlaybackState.Changing, MediaCapabilities.Stop)]
    [DataRow(MediaPlaybackState.Playing, MediaCapabilities.None)]
    public async Task StopOnlyConfirmationRequiresConsecutiveObservations(
        MediaPlaybackState interveningState, MediaCapabilities interveningControls)
    {
        var reads = 0;
        var result = await CreateController().ExecuteAsync(MediaOperation.Pause,
            _ =>
            {
                var read = Interlocked.Increment(ref reads);
                Assert.IsTrue(read <= 4, "The last two stop-only observations should finish readiness.");
                return Task.FromResult(read == 2
                    ? new Observation(interveningState, interveningControls)
                    : new Observation(MediaPlaybackState.Playing, MediaCapabilities.Stop));
            },
            (_, _, _) => throw new AssertFailedException("No usable Pause control was observed."), default);

        Assert.AreEqual(MediaBackendCommandStatus.Unsupported, result.Status);
        Assert.AreEqual(4, reads);
    }

    [TestMethod]
    [DataRow(MediaOperation.Play, MediaPlaybackState.Paused, MediaCapabilities.None)]
    [DataRow(MediaOperation.Pause, MediaPlaybackState.Playing, MediaCapabilities.None)]
    [DataRow(MediaOperation.Pause, MediaPlaybackState.Changing, MediaCapabilities.Stop)]
    [DataRow(MediaOperation.Pause, MediaPlaybackState.Unknown, MediaCapabilities.Stop)]
    public async Task DisabledDirectionWaitsForReadinessWithoutSendingEarly(
        MediaOperation intent, MediaPlaybackState initialState, MediaCapabilities initialCapabilities)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canSend = false;
        var sent = false;
        var desired = intent == MediaOperation.Play ? MediaPlaybackState.Playing : MediaPlaybackState.Paused;
        var capability = intent == MediaOperation.Play ? MediaCapabilities.Play : MediaCapabilities.Pause;
        var controller = CreateController();
        var transition = controller.ExecuteAsync(intent,
            _ =>
            {
                ready.TrySetResult();
                return Task.FromResult(new Observation(Volatile.Read(ref sent) ? desired : initialState,
                    Volatile.Read(ref canSend) ? capability : initialCapabilities));
            },
            (operation, markSending, _) =>
            {
                markSending();
                Assert.IsTrue(Volatile.Read(ref canSend));
                Assert.AreEqual(intent, operation);
                Volatile.Write(ref sent, true);
                return Task.FromResult(true);
            }, default);

        await ready.Task;
        Assert.IsFalse(transition.IsCompleted);
        Volatile.Write(ref canSend, true);
        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await transition.WaitAsync(TimeSpan.FromSeconds(2))).Status);
    }

    [TestMethod]
    [DataRow(MediaOperation.Play, MediaPlaybackState.Playing, MediaCapabilities.Pause)]
    [DataRow(MediaOperation.Pause, MediaPlaybackState.Paused, MediaCapabilities.Play | MediaCapabilities.Stop)]
    public async Task AlreadyMatchingObservationSkipsADisabledDirectionalControl(
        MediaOperation intent, MediaPlaybackState state, MediaCapabilities capabilities)
    {
        var senderInvoked = false;
        var result = await CreateController().ExecuteAsync(intent,
            _ => Task.FromResult(new Observation(state, capabilities)),
            (operation, _, _) =>
            {
                Assert.IsNull(operation);
                senderInvoked = true;
                return Task.FromResult(true);
            }, default);
        Assert.AreEqual(MediaBackendCommandStatus.Completed, result.Status);
        Assert.IsTrue(senderInvoked);
    }

    [TestMethod]
    public async Task AlreadySatisfiedIntentStillProcessesAncillaryPauses()
    {
        var gate = new GsmtcControlGate(NullLogger.Instance);
        var pausedSessions = 0;
        var primaryCalls = 0;
        var result = await CreateController().ExecuteAsync(MediaOperation.Play,
            _ => Task.FromResult(Observe(MediaPlaybackState.Playing)),
            (operation, markSending, token) => gate.RunCommandAsync(() =>
            {
                Assert.IsNull(operation);
                markSending();
                pausedSessions++;
                markSending();
                pausedSessions++;
                return GsmtcPlaybackController.SendRevalidatedAsync(MediaOperation.Play,
                    Observe(MediaPlaybackState.Playing), _ =>
                    {
                        primaryCalls++;
                        return Task.FromResult(false);
                    }, markSending);
            }, "Pause others", token), default);

        Assert.AreEqual(MediaBackendCommandStatus.Completed, result.Status);
        Assert.AreEqual(2, pausedSessions);
        Assert.AreEqual(0, primaryCalls);
    }

    [TestMethod]
    public async Task AlreadyMatchingObservationStillSendsAnEnabledAbsoluteIntent()
    {
        var operations = new List<MediaOperation>();
        var result = await CreateController().ExecuteAsync(MediaOperation.Play,
            _ => Task.FromResult(new Observation(MediaPlaybackState.Playing, MediaCapabilities.Play)),
            (operation, markSending, _) => { markSending(); operations.Add(operation!.Value); return Task.FromResult(true); }, default);
        Assert.AreEqual(MediaBackendCommandStatus.Completed, result.Status);
        CollectionAssert.AreEqual(new[] { MediaOperation.Play }, operations);
    }

    private static GsmtcPlaybackController CreateController() => new(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10));

    private static Observation Observe(MediaPlaybackState state) => new(state,
        state == MediaPlaybackState.Playing ? MediaCapabilities.Pause : MediaCapabilities.Play);
}