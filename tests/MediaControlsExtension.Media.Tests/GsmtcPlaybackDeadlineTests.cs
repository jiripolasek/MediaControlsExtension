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
public sealed class GsmtcPlaybackDeadlineTests
{
    [TestMethod]
    [DataRow(true, MediaBackendCommandStatus.Unconfirmed)]
    [DataRow(false, MediaBackendCommandStatus.Unavailable)]
    public async Task DeadlineDistinguishesAHungSendFromACallThatHasNotStarted(bool startNativeCall, MediaBackendCommandStatus expected)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new GsmtcControlGate(NullLogger.Instance);
        var controller = new GsmtcPlaybackController(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(10));
        var nativeCalls = 0;
        Task<bool>? nativeTask = null;
        try
        {
            var transition = controller.ExecuteAsync(MediaOperation.Pause,
                _ => Task.FromResult(new Observation(MediaPlaybackState.Playing, MediaCapabilities.Pause)),
                (_, markSending, token) => nativeTask = gate.RunCommandAsync(async () =>
                {
                    if (startNativeCall)
                    {
                        markSending();
                        Interlocked.Increment(ref nativeCalls);
                    }

                    entered.TrySetResult();
                    await release.Task;
                    if (!startNativeCall)
                    {
                        markSending();
                        Interlocked.Increment(ref nativeCalls);
                    }

                    return true;
                }, "Playback", token), default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var result = await transition.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(expected, result.Status);
            Assert.IsFalse(nativeTask!.IsCompleted);
            release.TrySetResult();
            if (startNativeCall)
            {
                Assert.IsTrue(await nativeTask.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            else
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => nativeTask);
            }

            Assert.AreEqual(startNativeCall ? 1 : 0, nativeCalls);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [TestMethod]
    public async Task BackgroundObservationDoesNotBlockPlaybackControlOrConfirmation()
    {
        var observations = new GsmtcObservationGate(NullLogger.Instance);
        var playbackObservations = new GsmtcObservationGate(NullLogger.Instance);
        var controls = new GsmtcControlGate(NullLogger.Instance);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var background = observations.RunAsync(async () =>
        {
            entered.TrySetResult();
            await release.Task;
            return true;
        }, "Artwork", default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var state = MediaPlaybackState.Playing;
        try
        {
            var result = await new GsmtcPlaybackController().ExecuteAsync(MediaOperation.Pause,
                token => playbackObservations.RunAsync(() => Task.FromResult(new Observation(state, MediaCapabilities.Pause)), "Read playback", token),
                (_, markSending, token) => controls.RunCommandAsync(() =>
                {
                    markSending();
                    state = MediaPlaybackState.Paused;
                    return Task.FromResult(true);
                }, "Pause", token), default).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.AreEqual(MediaBackendCommandStatus.Completed, result.Status);
            Assert.IsFalse(background.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await background.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [TestMethod]
    [DataRow(MediaPlaybackState.Paused, MediaCapabilities.TogglePlayback, MediaOperation.TogglePlayback, MediaBackendCommandStatus.Completed, 0)]
    [DataRow(MediaPlaybackState.Playing, MediaCapabilities.Pause, MediaOperation.Pause, MediaBackendCommandStatus.Completed, 1)]
    [DataRow(MediaPlaybackState.Playing, MediaCapabilities.TogglePlayback, MediaOperation.TogglePlayback, MediaBackendCommandStatus.Completed, 1)]
    [DataRow(MediaPlaybackState.Unknown, MediaCapabilities.TogglePlayback, MediaOperation.TogglePlayback, MediaBackendCommandStatus.Unavailable, 0)]
    public async Task ToggleFallbackRechecksStateAfterWaitingForTheControlGate(
        MediaPlaybackState latestState, MediaCapabilities latestCapabilities, MediaOperation expectedOperation,
        MediaBackendCommandStatus expectedStatus, int expectedCalls)
    {
        await AssertSendRevalidationAsync(MediaCapabilities.TogglePlayback, latestState, latestCapabilities,
            expectedOperation, expectedStatus, expectedCalls);
    }

    [TestMethod]
    [DataRow(MediaPlaybackState.Paused, MediaCapabilities.None, MediaOperation.Pause, 0)]
    [DataRow(MediaPlaybackState.Paused, MediaCapabilities.Pause, MediaOperation.Pause, 1)]
    [DataRow(MediaPlaybackState.Stopped, MediaCapabilities.None, MediaOperation.Pause, 0)]
    [DataRow(MediaPlaybackState.Stopped, MediaCapabilities.Pause, MediaOperation.Pause, 0)]
    [DataRow(MediaPlaybackState.Playing, MediaCapabilities.TogglePlayback, MediaOperation.TogglePlayback, 1)]
    public async Task DirectionalSendRechecksStateAndControlsAfterWaitingForTheControlGate(
        MediaPlaybackState latestState, MediaCapabilities latestCapabilities, MediaOperation expectedOperation, int expectedCalls)
    {
        await AssertSendRevalidationAsync(MediaCapabilities.Pause, latestState, latestCapabilities,
            expectedOperation, MediaBackendCommandStatus.Completed, expectedCalls);
    }

    [TestMethod]
    [DataRow(MediaCapabilities.Pause)]
    [DataRow(MediaCapabilities.TogglePlayback)]
    public async Task PauseSkipsStopOnlyControlsObservedUnderTheControlGate(MediaCapabilities readinessCapabilities)
    {
        await AssertSendRevalidationAsync(readinessCapabilities, MediaPlaybackState.Playing,
            MediaCapabilities.Play | MediaCapabilities.Stop, MediaOperation.Pause, MediaBackendCommandStatus.Unsupported, 0);
    }

    [TestMethod]
    [DataRow(true, MediaBackendCommandStatus.Completed)]
    [DataRow(false, MediaBackendCommandStatus.Unsupported)]
    public async Task StopOnlyRevalidationWaitsOutsideTheControlGate(bool pauseAppears, MediaBackendCommandStatus expected)
    {
        var controls = new GsmtcControlGate(NullLogger.Instance);
        var retryRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource<Observation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var controlReads = 0;
        var sends = 0;
        var firstStopOnlyAt = 0L;
        var nextReadAt = 0L;
        var transition = new GsmtcPlaybackController().ExecuteAsync(MediaOperation.Pause,
            _ =>
            {
                var read = Interlocked.Increment(ref reads);
                if (read == 2)
                {
                    nextReadAt = Stopwatch.GetTimestamp();
                    retryRead.TrySetResult();
                    return releaseRead.Task;
                }

                return Task.FromResult(new Observation(read == 1 ? MediaPlaybackState.Playing : MediaPlaybackState.Paused,
                    MediaCapabilities.Pause));
            },
            (_, markSending, token) => controls.RunCommandAsync(() =>
            {
                var first = Interlocked.Increment(ref controlReads) == 1;
                if (first)
                {
                    firstStopOnlyAt = Stopwatch.GetTimestamp();
                }

                return GsmtcPlaybackController.SendRevalidatedAsync(MediaOperation.Pause,
                    new(MediaPlaybackState.Playing, first ? MediaCapabilities.Stop : MediaCapabilities.Stop | MediaCapabilities.Pause),
                    operation =>
                    {
                        Assert.AreEqual(MediaOperation.Pause, operation);
                        Interlocked.Increment(ref sends);
                        return Task.FromResult(true);
                    }, markSending);
            }, "Pause", token), default);
        try
        {
            Assert.AreSame(retryRead.Task, await Task.WhenAny(retryRead.Task, transition).WaitAsync(TimeSpan.FromSeconds(2)),
                "A first stop-only observation must request another read.");
            Assert.IsTrue(Stopwatch.GetElapsedTime(firstStopOnlyAt, nextReadAt) >= TimeSpan.FromMilliseconds(40),
                "The default 50 ms poll must separate the reads, allowing for timer resolution.");
            Assert.IsTrue(await controls.RunCommandAsync(() => Task.FromResult(true), "Independent command", default)
                .WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.IsFalse(transition.IsCompleted);
            releaseRead.TrySetResult(new(MediaPlaybackState.Playing,
                pauseAppears ? MediaCapabilities.Stop | MediaCapabilities.Pause : MediaCapabilities.Stop));

            Assert.AreEqual(expected, (await transition.WaitAsync(TimeSpan.FromSeconds(2))).Status);
            Assert.AreEqual(pauseAppears ? 1 : 0, sends);
            Assert.AreEqual(pauseAppears ? 2 : 1, controlReads);
            Assert.AreEqual(pauseAppears ? 3 : 2, reads);
        }
        finally
        {
            releaseRead.TrySetResult(new(MediaPlaybackState.Playing, MediaCapabilities.Stop));
        }
    }

    [TestMethod]
    public async Task StopOnlyConfirmationCannotExtendTheTransitionDeadline()
    {
        var reads = 0;
        var controller = new GsmtcPlaybackController(TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(1));
        var result = await controller.ExecuteAsync(MediaOperation.Pause,
            _ =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult(new Observation(MediaPlaybackState.Playing, MediaCapabilities.Stop));
            },
            (_, _, _) => throw new AssertFailedException("Pause is not ready."), default)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(MediaBackendCommandStatus.Unavailable, result.Status);
        Assert.AreEqual(1, reads);
    }

    [TestMethod]
    public async Task StopOnlyRevalidationAfterAMutationDoesNotRetry()
    {
        var sends = 0;
        var result = await new GsmtcPlaybackController().ExecuteAsync(MediaOperation.Pause,
            _ => Task.FromResult(new Observation(MediaPlaybackState.Playing, MediaCapabilities.Pause)),
            (_, markSending, _) =>
            {
                markSending();
                Interlocked.Increment(ref sends);
                return GsmtcPlaybackController.SendRevalidatedAsync(MediaOperation.Pause,
                    new(MediaPlaybackState.Playing, MediaCapabilities.Stop),
                    _ => throw new AssertFailedException("No usable Pause control was observed."), markSending);
            }, default);

        Assert.AreEqual(MediaBackendCommandStatus.Unconfirmed, result.Status);
        Assert.AreEqual(1, sends);
    }

    private static async Task AssertSendRevalidationAsync(
        MediaCapabilities readinessCapabilities, MediaPlaybackState latestState, MediaCapabilities latestCapabilities,
        MediaOperation expectedOperation, MediaBackendCommandStatus expectedStatus, int expectedCalls)
    {
        var gate = new GsmtcControlGate(NullLogger.Instance);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = gate.RunCommandAsync(async () =>
        {
            entered.TrySetResult();
            await release.Task;
            return true;
        }, "Other session", default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var state = MediaPlaybackState.Playing;
        var reads = 0;
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new List<MediaOperation>();
        try
        {
            var transition = new GsmtcPlaybackController().ExecuteAsync(MediaOperation.Pause,
                _ => Task.FromResult(Interlocked.Increment(ref reads) == 1
                    ? new Observation(MediaPlaybackState.Playing, readinessCapabilities)
                    : new Observation(state, latestCapabilities)),
                (_, markSending, token) =>
                {
                    var pending = gate.RunCommandAsync(() => GsmtcPlaybackController.SendRevalidatedAsync(
                        MediaOperation.Pause, new(state, latestCapabilities), operation =>
                        {
                            operations.Add(operation);
                            state = MediaPlaybackState.Paused;
                            return Task.FromResult(true);
                        }, markSending), "Pause", token);
                    queued.TrySetResult();
                    return pending;
                }, default);
            await queued.Task.WaitAsync(TimeSpan.FromSeconds(2));
            state = latestState;
            release.TrySetResult();
            await blocker.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.AreEqual(expectedStatus, (await transition.WaitAsync(TimeSpan.FromSeconds(2))).Status);
            Assert.HasCount(expectedCalls, operations);
            if (expectedStatus == MediaBackendCommandStatus.Unsupported)
            {
                Assert.AreEqual(2, reads);
            }

            if (expectedCalls != 0)
            {
                Assert.AreEqual(expectedOperation, operations[0]);
            }
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [TestMethod]
    public async Task CircuitOpenBeforeSendingIsNotAnUnconfirmedOperation()
    {
        await Assert.ThrowsAsync<GsmtcControlCircuitOpenException>(() => new GsmtcPlaybackController().ExecuteAsync(MediaOperation.Pause,
            _ => Task.FromResult(new Observation(MediaPlaybackState.Playing, MediaCapabilities.Pause)),
            (_, _, _) => throw new GsmtcControlCircuitOpenException("Previous command", TimeSpan.Zero), default));

        var result = await new GsmtcPlaybackController().ExecuteAsync(MediaOperation.Pause,
            _ => Task.FromResult(new Observation(MediaPlaybackState.Playing, MediaCapabilities.Pause)),
            (_, markSending, _) =>
            {
                markSending();
                throw new GsmtcControlCircuitOpenException("Current command", TimeSpan.Zero);
            }, default);
        Assert.AreEqual(MediaBackendCommandStatus.Unconfirmed, result.Status);
    }
}