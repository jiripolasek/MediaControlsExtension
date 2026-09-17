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
    [DataRow(false)]
    [DataRow(true)]
    public async Task HungPlaybackReadIsBoundedPerControllerAndDoesNotBlockAnotherSession(bool hangDuringConfirmation)
    {
        var stalled = new GsmtcPlaybackController(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(5));
        var healthy = new GsmtcPlaybackController();
        var controls = new GsmtcControlGate(NullLogger.Instance);
        var release = new TaskCompletionSource<Observation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        Task<Observation> ReadStalledAsync(CancellationToken _) =>
            Interlocked.Increment(ref reads) == 1 && hangDuringConfirmation
                ? Task.FromResult(new Observation(MediaPlaybackState.Playing, MediaCapabilities.Pause))
                : release.Task;
        Task<bool> SendStalledAsync(MediaOperation? _, Action markSending, CancellationToken token) =>
            controls.RunCommandAsync(() =>
            {
                markSending();
                return Task.FromResult(true);
            }, "Pause stalled session", token);
        try
        {
            var first = await stalled.ExecuteAsync(MediaOperation.Pause, ReadStalledAsync, SendStalledAsync, default)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(hangDuringConfirmation ? MediaBackendCommandStatus.Unconfirmed : MediaBackendCommandStatus.Unavailable,
                first.Status);

            var healthyState = MediaPlaybackState.Playing;
            var other = await healthy.ExecuteAsync(MediaOperation.Pause,
                _ => Task.FromResult(new Observation(healthyState, MediaCapabilities.Pause)),
                (_, markSending, token) => controls.RunCommandAsync(() =>
                {
                    markSending();
                    healthyState = MediaPlaybackState.Paused;
                    return Task.FromResult(true);
                }, "Pause healthy session", token), default).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(MediaBackendCommandStatus.Completed, other.Status);
            Assert.IsFalse(release.Task.IsCompleted);

            var retry = await stalled.ExecuteAsync(MediaOperation.Pause, ReadStalledAsync, SendStalledAsync, default)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(MediaBackendCommandStatus.Unavailable, retry.Status);
            Assert.AreEqual(hangDuringConfirmation ? 2 : 1, Volatile.Read(ref reads));
            Assert.IsFalse(controls.IsCircuitOpen);

            release.TrySetResult(new(MediaPlaybackState.Paused, MediaCapabilities.Pause));
            using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            MediaBackendCommandResult recovered;
            do
            {
                await Task.Delay(10, recoveryTimeout.Token);
                recovered = await stalled.ExecuteAsync(MediaOperation.Pause, ReadStalledAsync, SendStalledAsync, recoveryTimeout.Token);
            }
            while (recovered.Status == MediaBackendCommandStatus.Unavailable);

            Assert.AreEqual(MediaBackendCommandStatus.Completed, recovered.Status);
        }
        finally
        {
            release.TrySetResult(new(MediaPlaybackState.Paused, MediaCapabilities.Pause));
        }
    }

    [TestMethod]
    public async Task TimedOutPlaybackReadRecoversWithoutOpeningTheControlCircuit()
    {
        var observations = new GsmtcObservationGate(NullLogger.Instance, TimeSpan.FromMilliseconds(100), TimeSpan.Zero);
        var controls = new GsmtcControlGate(NullLogger.Instance);
        var release = new TaskCompletionSource<Observation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = false;
        var reads = 0;
        Task<Observation> ReadAsync(CancellationToken token) => observations.RunAsync(() =>
        {
            Interlocked.Increment(ref reads);
            return sent ? release.Task : Task.FromResult(new Observation(MediaPlaybackState.Playing, MediaCapabilities.Pause));
        }, "Playback", token);
        Task<bool> SendAsync(MediaOperation? _, Action markSending, CancellationToken token) => controls.RunCommandAsync(() =>
        {
            markSending();
            sent = true;
            return Task.FromResult(true);
        }, "Pause", token);
        try
        {
            var result = await new GsmtcPlaybackController().ExecuteAsync(MediaOperation.Pause, ReadAsync, SendAsync, default)
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.AreEqual(MediaBackendCommandStatus.Unconfirmed, result.Status);
            Assert.IsTrue(observations.IsBlocked);
            Assert.AreEqual(2, reads);
            Assert.IsTrue(await controls.RunCommandAsync(() => Task.FromResult(true), "SkipNext", default));
            Assert.IsFalse(controls.IsCircuitOpen);
            release.TrySetResult(new(MediaPlaybackState.Paused, MediaCapabilities.Play | MediaCapabilities.Pause));
            using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (observations.IsBlocked)
            {
                await Task.Delay(10, recoveryTimeout.Token);
            }

            var recovered = await new GsmtcPlaybackController().ExecuteAsync(MediaOperation.Pause, ReadAsync, SendAsync, default)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(MediaBackendCommandStatus.Completed, recovered.Status);
            Assert.IsFalse(controls.IsCircuitOpen);
        }
        finally
        {
            release.TrySetResult(new(MediaPlaybackState.Paused, MediaCapabilities.Pause));
        }
    }

    [TestMethod]
    public async Task ConfirmationDoesNotQueueBehindABusyNativeCommand()
    {
        var observations = new GsmtcObservationGate(NullLogger.Instance);
        var controls = new GsmtcControlGate(NullLogger.Instance);
        var confirming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowConfirmation = new TaskCompletionSource<Observation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOther = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        Task<bool>? other = null;
        var transition = new GsmtcPlaybackController().ExecuteAsync(MediaOperation.Pause,
            token => observations.RunAsync(() =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    return Task.FromResult(new Observation(MediaPlaybackState.Playing, MediaCapabilities.Pause));
                }

                confirming.TrySetResult();
                return allowConfirmation.Task;
            }, "Playback", token),
            (_, markSending, token) => controls.RunCommandAsync(() =>
            {
                markSending();
                return Task.FromResult(true);
            }, "Pause", token), default);
        try
        {
            await confirming.Task.WaitAsync(TimeSpan.FromSeconds(2));
            other = controls.RunCommandAsync(async () =>
            {
                otherStarted.TrySetResult();
                await releaseOther.Task;
                return true;
            }, "Other session command", default);
            await otherStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            allowConfirmation.TrySetResult(new(MediaPlaybackState.Paused, MediaCapabilities.Pause));

            Assert.AreEqual(MediaBackendCommandStatus.Completed, (await transition.WaitAsync(TimeSpan.FromSeconds(2))).Status);
            Assert.IsFalse(other.IsCompleted);
        }
        finally
        {
            allowConfirmation.TrySetResult(new(MediaPlaybackState.Paused, MediaCapabilities.Pause));
            releaseOther.TrySetResult();
            if (other is not null)
            {
                await other.WaitAsync(TimeSpan.FromSeconds(2));
            }

            await transition.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }
}