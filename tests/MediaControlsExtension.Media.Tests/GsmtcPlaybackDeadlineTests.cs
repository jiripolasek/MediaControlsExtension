// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Gsmtc;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Observation = JPSoftworks.MediaControlsExtension.Media.Gsmtc.GsmtcPlaybackController.PlaybackObservation;
using SendResult = JPSoftworks.MediaControlsExtension.Media.Gsmtc.GsmtcPlaybackController.PlaybackSendResult;
using SendStatus = JPSoftworks.MediaControlsExtension.Media.Gsmtc.GsmtcPlaybackController.PlaybackSendStatus;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class GsmtcPlaybackDeadlineTests
{
    [TestMethod]
    [DataRow(0, 600)]
    [DataRow(300, 300)]
    public async Task MissingEventFallbackWaitsAfterSlowSendCompletion(int gateWaitMilliseconds, int sendMilliseconds)
    {
        var session = new PlaybackTestSession();
        var entered = PlaybackTestSession.Signal();
        var release = PlaybackTestSession.Signal();
        var blocker = session.Controls.RunCommandAsync(async () =>
        {
            entered.TrySetResult();
            await release.Task;
            return true;
        }, "Other session", default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task? stateChange = null;
        session.NativeSend = async _ =>
        {
            await Task.Delay(sendMilliseconds);
            stateChange = Task.Run(async () =>
            {
                await Task.Delay(100);
                session.Value = new(MediaPlaybackState.Paused, MediaCapabilities.Play);
            });
            return true;
        };
        try
        {
            var transition = session.ExecuteAsync();
            await Task.Delay(gateWaitMilliseconds);
            release.TrySetResult();
            await blocker;

            Assert.AreEqual(MediaBackendCommandStatus.Completed, (await transition).Status);
            Assert.AreEqual(2, session.Reads);
            Assert.HasCount(1, session.Operations);
        }
        finally
        {
            release.TrySetResult();
            if (stateChange is not null)
            {
                await stateChange;
            }
        }
    }

    [TestMethod]
    public async Task PostSendFallbackCannotExtendTheOriginalDeadline()
    {
        var session = new PlaybackTestSession
        {
            Controller = new(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(200)),
        };
        session.NativeSend = async _ =>
        {
            await Task.Delay(200);
            return true;
        };

        Assert.AreEqual(MediaBackendCommandStatus.Unconfirmed, (await session.ExecuteAsync()).Status);
        Assert.AreEqual(1, session.Reads);
        Assert.HasCount(1, session.Operations);
    }

    [TestMethod]
    [DataRow(true, MediaBackendCommandStatus.Unconfirmed)]
    [DataRow(false, MediaBackendCommandStatus.Unavailable)]
    public async Task DeadlineDistinguishesAHungSendFromACallThatHasNotStarted(bool startNativeCall, MediaBackendCommandStatus expected)
    {
        var entered = PlaybackTestSession.Signal();
        var release = PlaybackTestSession.Signal();
        var gate = new GsmtcControlGate(NullLogger.Instance);
        var observations = new GsmtcPlaybackObservations(new Lock());
        var controller = new GsmtcPlaybackController(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(50));
        var nativeCalls = 0;
        Task<SendResult>? nativeTask = null;
        try
        {
            var transition = controller.ExecuteAsync(MediaOperation.Pause, observations,
                () => throw new AssertFailedException("The send has not completed."),
                (markSending, token) => nativeTask = gate.RunCommandAsync(async () =>
                {
                    var sample = observations.ReadCommand(() => new(MediaPlaybackState.Playing, MediaCapabilities.Pause));
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

                    return new SendResult(SendStatus.Sent, sample, sample.Sequence);
                }, "Playback", token), default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.AreEqual(expected, (await transition.WaitAsync(TimeSpan.FromSeconds(2))).Status);
            Assert.IsFalse(nativeTask!.IsCompleted);
            release.TrySetResult();
            if (startNativeCall)
            {
                Assert.AreEqual(SendStatus.Sent, (await nativeTask.WaitAsync(TimeSpan.FromSeconds(2))).Status);
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
        var entered = PlaybackTestSession.Signal();
        var release = PlaybackTestSession.Signal();
        var background = observations.RunAsync(async () =>
        {
            entered.TrySetResult();
            await release.Task;
            return true;
        }, "Artwork", default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            var session = new PlaybackTestSession();
            Assert.AreEqual(MediaBackendCommandStatus.Completed, (await session.ExecuteAsync()).Status);
            Assert.AreEqual(2, session.Reads);
            Assert.IsFalse(background.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await background.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [TestMethod]
    [DataRow(MediaPlaybackState.Paused, MediaCapabilities.TogglePlayback, MediaOperation.Pause, MediaBackendCommandStatus.Completed, 0)]
    [DataRow(MediaPlaybackState.Paused, MediaCapabilities.None, MediaOperation.Pause, MediaBackendCommandStatus.Completed, 0)]
    [DataRow(MediaPlaybackState.Paused, MediaCapabilities.Pause, MediaOperation.Pause, MediaBackendCommandStatus.Completed, 1)]
    [DataRow(MediaPlaybackState.Stopped, MediaCapabilities.None, MediaOperation.Pause, MediaBackendCommandStatus.Completed, 0)]
    [DataRow(MediaPlaybackState.Stopped, MediaCapabilities.Pause, MediaOperation.Pause, MediaBackendCommandStatus.Completed, 0)]
    [DataRow(MediaPlaybackState.Playing, MediaCapabilities.Pause, MediaOperation.Pause, MediaBackendCommandStatus.Completed, 1)]
    [DataRow(MediaPlaybackState.Playing, MediaCapabilities.TogglePlayback, MediaOperation.TogglePlayback, MediaBackendCommandStatus.Completed, 1)]
    [DataRow(MediaPlaybackState.Playing, MediaCapabilities.Stop, MediaOperation.Pause, MediaBackendCommandStatus.Unsupported, 0)]
    [DataRow(MediaPlaybackState.Unknown, MediaCapabilities.TogglePlayback, MediaOperation.Pause, MediaBackendCommandStatus.Unavailable, 0)]
    public async Task RevalidationUsesCurrentStateAndControlsAfterWaitingForTheGate(
        MediaPlaybackState state, MediaCapabilities capabilities, MediaOperation expectedOperation,
        MediaBackendCommandStatus expectedStatus, int sends)
    {
        var session = new PlaybackTestSession { Controller = new(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(100)) };
        session.Observations.ReadCommand(() => session.Value);
        var entered = PlaybackTestSession.Signal();
        var release = PlaybackTestSession.Signal();
        var blocker = session.Controls.RunCommandAsync(async () =>
        {
            entered.TrySetResult();
            await release.Task;
            return true;
        }, "Other session", default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            var transition = session.ExecuteAsync();
            session.Value = new(state, capabilities);
            release.TrySetResult();
            await blocker;
            Assert.AreEqual(expectedStatus, (await transition).Status);
            Assert.HasCount(sends, session.Operations);
            if (sends > 0)
            {
                CollectionAssert.AreEqual(new[] { expectedOperation }, session.Operations.ToArray());
            }
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [TestMethod]
    [DataRow(true, MediaBackendCommandStatus.Completed)]
    [DataRow(false, MediaBackendCommandStatus.Unsupported)]
    public async Task StopOnlySecondObservationRunsOutsideTheControlGate(bool pauseAppears, MediaBackendCommandStatus expected)
    {
        var session = new PlaybackTestSession { Value = new(MediaPlaybackState.Playing, MediaCapabilities.Stop) };
        var entered = PlaybackTestSession.Signal();
        var release = PlaybackTestSession.Signal();
        session.SharedRead = async () =>
        {
            if (session.Reads == 2)
            {
                entered.TrySetResult();
                await release.Task;
                if (pauseAppears)
                {
                    session.Value = session.Value with { Capabilities = MediaCapabilities.Pause };
                }
            }

            return session.Value;
        };
        var transition = session.ExecuteAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsTrue(await session.Controls.RunCommandAsync(() => Task.FromResult(true), "Independent", default)
                .WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.IsFalse(transition.IsCompleted);
            release.TrySetResult();
            Assert.AreEqual(expected, (await transition).Status);
            Assert.AreEqual(pauseAppears ? 4 : 2, session.Reads);
            Assert.AreEqual(pauseAppears ? 2 : 1, session.CommandReads);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [TestMethod]
    public async Task StopOnlyConfirmationCannotExtendTheTransitionDeadline()
    {
        var session = new PlaybackTestSession
        {
            Value = new(MediaPlaybackState.Playing, MediaCapabilities.Stop),
            Controller = new(TimeSpan.FromMilliseconds(25), TimeSpan.FromSeconds(1)),
        };

        Assert.AreEqual(MediaBackendCommandStatus.Unavailable, (await session.ExecuteAsync()).Status);
        Assert.AreEqual(1, session.Reads);
        Assert.IsEmpty(session.Operations);
    }

    [TestMethod]
    public async Task StopOnlyRevalidationAfterAMutationDoesNotRetry()
    {
        var session = new PlaybackTestSession { Value = new(MediaPlaybackState.Playing, MediaCapabilities.Stop) };
        var mutations = 0;
        session.BeforeRevalidation = (markSending, _) =>
        {
            markSending();
            mutations++;
            return Task.CompletedTask;
        };

        Assert.AreEqual(MediaBackendCommandStatus.Unconfirmed, (await session.ExecuteAsync()).Status);
        Assert.AreEqual(1, mutations);
        Assert.AreEqual(1, session.Reads);
    }

    [TestMethod]
    public async Task ReadinessFallbackUsesTheOriginalDeadlineAndOnlyTwoReads()
    {
        var session = new PlaybackTestSession
        {
            Value = new(MediaPlaybackState.Playing, MediaCapabilities.None),
            Controller = new(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(30)),
        };

        Assert.AreEqual(MediaBackendCommandStatus.Unavailable, (await session.ExecuteAsync()).Status);
        Assert.AreEqual(2, session.Reads);
        Assert.AreEqual(2, session.CommandReads);
        Assert.IsEmpty(session.Operations);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CircuitFailureIsUnconfirmedOnlyAfterMutation(bool mutate)
    {
        var session = new PlaybackTestSession();
        session.BeforeRevalidation = (markSending, _) =>
        {
            if (mutate)
            {
                markSending();
            }

            throw new GsmtcControlCircuitOpenException("Playback", TimeSpan.Zero);
        };
        if (mutate)
        {
            Assert.AreEqual(MediaBackendCommandStatus.Unconfirmed, (await session.ExecuteAsync()).Status);
        }
        else
        {
            await Assert.ThrowsAsync<GsmtcControlCircuitOpenException>(() => session.ExecuteAsync());
        }

        Assert.AreEqual(0, session.Reads);
    }
}