// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Gsmtc;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;
using Changes = JPSoftworks.MediaControlsExtension.Media.Gsmtc.GsmtcBackend.SessionObservationChanges;
using Observation = JPSoftworks.MediaControlsExtension.Media.Gsmtc.GsmtcPlaybackController.PlaybackObservation;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class GsmtcSessionSnapshotTests
{
    [TestMethod]
    public async Task RetirementDuringPlaybackSkipsWarningsAndRemainingNativeParts()
    {
        var session = new SnapshotSession();
        var snapshot = session.ReadAsync();
        try
        {
            await session.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            session.Observations.Retire();
            await Assert.ThrowsExactlyAsync<GsmtcSessionRetiredException>(() => snapshot);
            Assert.IsEmpty(session.Failures);
            Assert.AreEqual(0, session.TimelineReads);
            Assert.AreEqual(0, session.MediaReads);
        }
        finally
        {
            session.Release.TrySetResult(new(MediaPlaybackState.Paused, MediaCapabilities.Play));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TimedOutPlaybackDefersOtherNativePartsAndPreservesSeededScalarsUntilRecovery(bool failRead)
    {
        var session = new SnapshotSession();
        var snapshot = session.ReadAsync();
        try
        {
            await session.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            session.Timeout.TrySetResult();
            Assert.AreEqual(session.Previous, await snapshot);
            Assert.IsTrue(session.Observations.IsSnapshotSuspended);
            for (var i = 0; i < 3; i++)
            {
                Assert.AreEqual(session.Previous, await session.ReadAsync());
            }

            Assert.HasCount(1, session.Failures);
            Assert.IsInstanceOfType<TimeoutException>(session.Failures[0]);
            Assert.AreEqual(1, session.PlaybackReads);
            Assert.AreEqual(0, session.TimelineReads);
            Assert.AreEqual(0, session.MediaReads);
            Assert.AreEqual(Changes.Timeline | Changes.MediaProperties, session.Deferred);
            if (failRead)
            {
                session.Release.TrySetException(new InvalidOperationException("Late playback failure"));
            }
            else
            {
                session.Release.TrySetResult(new(MediaPlaybackState.Paused, MediaCapabilities.Play));
            }

            await session.Resumed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsFalse(session.Observations.IsSnapshotSuspended);
            var recovered = await session.ReadAsync();
            Assert.AreEqual(failRead ? session.Previous.PlaybackState : MediaPlaybackState.Paused, recovered.PlaybackState);
            Assert.AreEqual(failRead ? session.Previous.Capabilities : MediaCapabilities.Play, recovered.Capabilities);
            Assert.AreEqual(1, session.TimelineReads);
            Assert.AreEqual(1, session.MediaReads);
            Assert.AreEqual(1, session.PlaybackReads);
            Assert.HasCount(1, session.Failures);
        }
        finally
        {
            session.Release.TrySetResult(new(MediaPlaybackState.Paused, MediaCapabilities.Play));
        }
    }

    [TestMethod]
    public async Task TransientPlaybackFailureRetainsSeedAndDoesNotReplayItsWarning()
    {
        var session = new SnapshotSession();
        session.Release.TrySetException(new InvalidOperationException("Transient playback failure"));
        Assert.AreEqual(session.Previous, await session.ReadAsync());
        Assert.AreEqual(session.Previous, await session.ReadAsync());
        Assert.HasCount(1, session.Failures);
        Assert.AreEqual(1, session.PlaybackReads);
        Assert.AreEqual(2, session.TimelineReads);
        Assert.AreEqual(2, session.MediaReads);
    }

    private sealed class SnapshotSession
    {
        public GsmtcPlaybackObservations Observations { get; }
        public MediaBackendSessionSnapshot Previous { get; } = FakeMediaBackend.CreateSnapshot(1, "Player").Sessions[0];
        public TaskCompletionSource Entered { get; } = PlaybackTestSession.Signal();
        public TaskCompletionSource Timeout { get; } = PlaybackTestSession.Signal();
        public TaskCompletionSource Resumed { get; } = PlaybackTestSession.Signal();
        public TaskCompletionSource<Observation> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<Exception> Failures { get; } = [];
        public Changes Deferred;
        public int PlaybackReads;
        public int TimelineReads;
        public int MediaReads;

        public SnapshotSession()
        {
            this.Observations = new(new Lock(), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2),
                () => this.Resumed.TrySetResult(), delayUntilTimeout: (timeout, _, token) => timeout == TimeSpan.FromSeconds(1)
                    ? this.Timeout.Task.WaitAsync(token) : Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token));
        }

        public Task<MediaBackendSessionSnapshot> ReadAsync() => GsmtcBackend.ReadSessionPartsAsync(
            this.Previous, Changes.All, this.Observations,
            () =>
            {
                this.PlaybackReads++;
                this.Entered.TrySetResult();
                return this.Release.Task;
            },
            () =>
            {
                this.TimelineReads++;
                return this.Previous.TimelineProperties;
            },
            () =>
            {
                this.MediaReads++;
                return Task.FromResult(this.Previous.MediaProperties);
            },
            changes => this.Deferred |= changes,
            (_, exception) => this.Failures.Add(exception), default);
    }
}