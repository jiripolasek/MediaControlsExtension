// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.ITunes;
using JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class ITunesArtworkTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LateArtworkAppearsOnRefresh(bool missingCollection)
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await ITunesNativeFixture.CreateAsync(timeProvider: clock);
        fixture.HasArtworkCollection = !missingCollection;
        fixture.HasArtwork = false;
        await fixture.RefreshAsync();
        var before = await fixture.Backend.ReadSnapshotAsync(default);
        Assert.IsNull(before.Sessions.Single().MediaProperties.Artwork);
        Assert.AreEqual(1, fixture.ArtworkCollectionReads);
        Assert.AreEqual(0, fixture.ArtworkExports);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var updates = fixture.Backend.WatchAsync(timeout.Token).GetAsyncEnumerator();
        Assert.IsTrue(await updates.MoveNextAsync());

        fixture.HasArtworkCollection = true;
        fixture.HasArtwork = true;
        clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.SendPlaybackEventAsync(ITunesEventDispId.PlayerPlayingTrackInfoChanged);

        var after = await fixture.Backend.ReadSnapshotAsync(default);
        var key = after.Sessions.Single().MediaProperties.Artwork;
        Assert.IsNotNull(key);
        var artwork = await fixture.Backend.GetArtworkAsync(key.Value, default);
        Assert.IsNotNull(artwork);
        CollectionAssert.AreEqual(fixture.ArtworkData, artwork.Data.ToArray());
        Assert.AreEqual(before.Sessions.Single().BindingGeneration, after.Sessions.Single().BindingGeneration);
        Assert.IsGreaterThan(before.Revision, after.Revision);
        Assert.IsTrue(await updates.MoveNextAsync());
        Assert.IsTrue(updates.Current.HasFlag(MediaBackendSignal.ObservationsChanged));
        Assert.AreEqual(2, fixture.ArtworkCollectionReads);
        Assert.AreEqual(1, fixture.ArtworkExports);
        fixture.AssertArtworkFilesDeleted();
        fixture.AssertTemporaryObjectsReleased();
    }

    [TestMethod]
    [DataRow("hresult")]
    [DataRow("missing-file")]
    [DataRow("empty-file")]
    [DataRow("locked-file")]
    public async Task FailedExportsRecoverWithoutHidingPlayback(string failure)
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await ITunesNativeFixture.CreateAsync(timeProvider: clock);
        fixture.ArtworkSaveResult = failure == "hresult" ? unchecked((int)0x80004005) : 0;
        fixture.WriteArtworkFile = failure != "missing-file";
        fixture.ArtworkData = failure == "empty-file" ? [] : [1, 2, 3];
        fixture.LockArtworkFile = failure == "locked-file";
        await fixture.RefreshAsync();

        var before = await fixture.Backend.ReadSnapshotAsync(default);
        Assert.AreEqual(MediaBackendConnectionState.Connected, before.Connection);
        Assert.HasCount(1, before.Sessions);
        Assert.AreEqual("Track", before.Sessions.Single().MediaProperties.Title);
        Assert.AreEqual(MediaPlaybackState.Playing, before.Sessions.Single().PlaybackState);
        Assert.IsNull(before.Sessions.Single().MediaProperties.Artwork);
        Assert.AreEqual(1, fixture.ArtworkExports);
        fixture.AssertTemporaryObjectsReleased();
        if (!fixture.LockArtworkFile)
        {
            fixture.AssertArtworkFilesDeleted();
        }

        fixture.ReleaseArtworkFileLocks();
        fixture.ArtworkSaveResult = 0;
        fixture.WriteArtworkFile = true;
        fixture.LockArtworkFile = false;
        fixture.ArtworkData = [9, 8, 7];
        clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.RefreshAsync();

        var after = await fixture.Backend.ReadSnapshotAsync(default);
        var key = after.Sessions.Single().MediaProperties.Artwork;
        Assert.IsNotNull(key);
        var artwork = await fixture.Backend.GetArtworkAsync(key.Value, default);
        Assert.IsNotNull(artwork);
        CollectionAssert.AreEqual(fixture.ArtworkData, artwork.Data.ToArray());
        Assert.IsGreaterThan(before.Revision, after.Revision);
        Assert.AreEqual(2, fixture.ArtworkExports);
        fixture.AssertArtworkFilesDeleted();
        fixture.AssertTemporaryObjectsReleased();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RetriesRespectBackoffAndStopAfterFourAttempts(bool failedExport)
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await ITunesNativeFixture.CreateAsync(timeProvider: clock);
        fixture.HasArtwork = failedExport;
        fixture.ArtworkSaveResult = unchecked((int)0x80004005);
        await fixture.RefreshAsync();

        var attempts = 1;
        foreach (var seconds in new[] { 1, 2, 4 })
        {
            clock.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromTicks(1));
            for (var refresh = 0; refresh < 10; refresh++)
            {
                await fixture.RefreshAsync();
            }

            Assert.AreEqual(attempts, fixture.ArtworkCollectionReads);
            clock.Advance(TimeSpan.FromTicks(1));
            await fixture.RefreshAsync();
            Assert.AreEqual(++attempts, fixture.ArtworkCollectionReads);
        }

        fixture.HasArtwork = true;
        fixture.ArtworkSaveResult = 0;
        for (var refresh = 0; refresh < 10; refresh++)
        {
            clock.Advance(TimeSpan.FromHours(1));
            await fixture.RefreshAsync();
        }

        Assert.AreEqual(4, fixture.ArtworkCollectionReads);
        Assert.AreEqual(failedExport ? 4 : 0, fixture.ArtworkExports);
        Assert.IsNull((await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single().MediaProperties.Artwork);
        fixture.AssertArtworkFilesDeleted();
        fixture.AssertTemporaryObjectsReleased();
    }

    [TestMethod]
    [DataRow(MediaOperation.Play)]
    [DataRow(MediaOperation.Pause)]
    [DataRow(MediaOperation.Stop)]
    [DataRow(MediaOperation.SkipNext)]
    [DataRow(MediaOperation.SkipPrevious)]
    [DataRow(MediaOperation.ToggleShuffle)]
    [DataRow(MediaOperation.ToggleRepeat)]
    public async Task CommandConfirmationDoesNotRetryArtwork(MediaOperation operation)
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await ITunesNativeFixture.CreateAsync(timeProvider: clock);
        fixture.ApplyPlaybackCommands = operation != MediaOperation.Stop;
        fixture.HasArtwork = false;
        await fixture.RefreshAsync();
        var before = await fixture.Backend.ReadSnapshotAsync(default);
        fixture.HasArtwork = true;
        clock.Advance(TimeSpan.FromSeconds(1));

        var result = await fixture.ExecuteAsync(operation);

        Assert.AreEqual(MediaBackendCommandStatus.Completed, result.Status);
        Assert.AreEqual(1, fixture.ArtworkCollectionReads);
        Assert.AreEqual(0, fixture.ArtworkExports);
        var after = await fixture.Backend.ReadSnapshotAsync(default);
        Assert.IsNull(after.Sessions.Single().MediaProperties.Artwork);
        if (operation is MediaOperation.Play or MediaOperation.Pause)
        {
            Assert.IsGreaterThan(before.Sessions.Single().PlaybackObservationRevision, result.ConfirmedPlaybackRevision);
            Assert.AreEqual(result.ConfirmedPlaybackRevision, after.Sessions.Single().PlaybackObservationRevision);
        }

        await fixture.RefreshAsync();

        var key = (await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single().MediaProperties.Artwork;
        Assert.IsNotNull(key);
        var artwork = await fixture.Backend.GetArtworkAsync(key.Value, default);
        Assert.IsNotNull(artwork);
        CollectionAssert.AreEqual(fixture.ArtworkData, artwork.Data.ToArray());
        Assert.AreEqual(2, fixture.ArtworkCollectionReads);
        Assert.AreEqual(1, fixture.ArtworkExports);
        fixture.AssertArtworkFilesDeleted();
        fixture.AssertTemporaryObjectsReleased();
    }

    [TestMethod]
    [DataRow(MediaOperation.SkipNext)]
    [DataRow(MediaOperation.SkipPrevious)]
    public async Task CommandRefreshStillReadsArtworkForANewTrack(MediaOperation operation)
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await ITunesNativeFixture.CreateAsync(timeProvider: clock);
        fixture.HasArtwork = false;
        await fixture.RefreshAsync();
        fixture.TrackId = 2;
        fixture.HasArtwork = true;

        var result = await fixture.ExecuteAsync(operation);

        Assert.AreEqual(MediaBackendCommandStatus.Completed, result.Status);
        var key = (await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single().MediaProperties.Artwork;
        Assert.IsNotNull(key);
        var artwork = await fixture.Backend.GetArtworkAsync(key.Value, default);
        Assert.IsNotNull(artwork);
        CollectionAssert.AreEqual(fixture.ArtworkData, artwork.Data.ToArray());
        Assert.AreEqual(2, fixture.ArtworkCollectionReads);
        Assert.AreEqual(1, fixture.ArtworkExports);
        fixture.AssertArtworkFilesDeleted();
        fixture.AssertTemporaryObjectsReleased();
    }

    [TestMethod]
    public async Task SuccessfulArtworkStaysCached()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await ITunesNativeFixture.CreateAsync(timeProvider: clock);
        await fixture.RefreshAsync();
        var before = await fixture.Backend.ReadSnapshotAsync(default);
        var key = before.Sessions.Single().MediaProperties.Artwork;
        Assert.IsNotNull(key);
        var artwork = await fixture.Backend.GetArtworkAsync(key.Value, default);
        Assert.IsNotNull(artwork);

        fixture.ArtworkData = [9, 8, 7];
        for (var refresh = 0; refresh < 10; refresh++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await fixture.RefreshAsync();
        }

        var after = await fixture.Backend.ReadSnapshotAsync(default);
        Assert.AreEqual(key, after.Sessions.Single().MediaProperties.Artwork);
        Assert.AreSame(artwork, await fixture.Backend.GetArtworkAsync(key.Value, default));
        Assert.AreEqual(before.Revision, after.Revision);
        Assert.AreEqual(1, fixture.ArtworkCollectionReads);
        Assert.AreEqual(1, fixture.ArtworkExports);
        fixture.AssertArtworkFilesDeleted();
        fixture.AssertTemporaryObjectsReleased();
    }

    [TestMethod]
    public async Task TrackChangeDiscardsThePreviousRetry()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await ITunesNativeFixture.CreateAsync(timeProvider: clock);
        fixture.HasArtwork = false;
        await fixture.RefreshAsync();
        clock.Advance(TimeSpan.FromMilliseconds(500));
        fixture.TrackId = 2;
        await fixture.RefreshAsync();
        Assert.AreEqual(2, fixture.ArtworkCollectionReads);

        fixture.HasArtwork = true;
        fixture.ArtworkData = [2, 2, 2];
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await fixture.RefreshAsync();
        Assert.AreEqual(2, fixture.ArtworkCollectionReads);
        Assert.AreEqual(0, fixture.ArtworkExports);
        Assert.IsNull((await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single().MediaProperties.Artwork);

        clock.Advance(TimeSpan.FromMilliseconds(500));
        await fixture.RefreshAsync();
        var key = (await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single().MediaProperties.Artwork;
        Assert.IsNotNull(key);
        var artwork = await fixture.Backend.GetArtworkAsync(key.Value, default);
        Assert.IsNotNull(artwork);
        CollectionAssert.AreEqual(new byte[] { 2, 2, 2 }, artwork.Data.ToArray());
        Assert.AreEqual(3, fixture.ArtworkCollectionReads);
        Assert.AreEqual(1, fixture.ArtworkExports);
        fixture.AssertArtworkFilesDeleted();
        fixture.AssertTemporaryObjectsReleased();
    }

    [TestMethod]
    [DataRow("track-change")]
    [DataRow("track-removed")]
    [DataRow("reconnect")]
    public async Task TrackAndConnectionChangesResetAnExhaustedBudget(string change)
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await ITunesNativeFixture.CreateAsync(timeProvider: clock);
        fixture.HasArtwork = false;
        await fixture.RefreshAsync();
        foreach (var seconds in new[] { 1, 2, 4 })
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            await fixture.RefreshAsync();
        }

        Assert.AreEqual(4, fixture.ArtworkCollectionReads);
        switch (change)
        {
            case "track-change":
                fixture.TrackId = 2;
                break;
            case "track-removed":
                fixture.HasCurrentTrack = false;
                await fixture.RefreshAsync();
                Assert.IsEmpty((await fixture.Backend.ReadSnapshotAsync(default)).Sessions);
                fixture.HasCurrentTrack = true;
                break;
            case "reconnect":
                await fixture.ReconnectAsync();
                break;
        }

        fixture.HasArtwork = true;
        await fixture.RefreshAsync();
        Assert.IsNotNull((await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single().MediaProperties.Artwork);
        Assert.AreEqual(5, fixture.ArtworkCollectionReads);
        Assert.AreEqual(1, fixture.ArtworkExports);
        fixture.AssertArtworkFilesDeleted();
        fixture.AssertTemporaryObjectsReleased();
    }

    [TestMethod]
    public async Task TrackChangeRetiresTheOldArtworkKeyBeforeRetry()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await ITunesNativeFixture.CreateAsync(timeProvider: clock);
        await fixture.RefreshAsync();
        var oldKey = (await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single().MediaProperties.Artwork;
        Assert.IsNotNull(oldKey);
        fixture.TrackId = 2;
        fixture.HasArtwork = false;
        await fixture.RefreshAsync();
        Assert.IsNull((await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single().MediaProperties.Artwork);
        Assert.IsNull(await fixture.Backend.GetArtworkAsync(oldKey.Value, default));

        fixture.HasArtwork = true;
        fixture.ArtworkData = [2, 2, 2];
        clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.RefreshAsync();
        var newKey = (await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single().MediaProperties.Artwork;
        Assert.IsNotNull(newKey);
        Assert.AreNotEqual(oldKey, newKey);
        Assert.IsNull(await fixture.Backend.GetArtworkAsync(oldKey.Value, default));
        var artwork = await fixture.Backend.GetArtworkAsync(newKey.Value, default);
        Assert.IsNotNull(artwork);
        CollectionAssert.AreEqual(new byte[] { 2, 2, 2 }, artwork.Data.ToArray());
        Assert.AreEqual(3, fixture.ArtworkCollectionReads);
        Assert.AreEqual(2, fixture.ArtworkExports);
        fixture.AssertArtworkFilesDeleted();
        fixture.AssertTemporaryObjectsReleased();
    }

    [TestMethod]
    [DataRow(8)]
    [DataRow(9)]
    public async Task QuitPreventsAnEligibleRetry(int eventId)
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await ITunesNativeFixture.CreateAsync(eventId, timeProvider: clock);
        fixture.HasArtwork = false;
        await fixture.RefreshAsync();
        fixture.HasArtwork = true;
        clock.Advance(TimeSpan.FromSeconds(1));

        await fixture.RunAsync(() =>
        {
            fixture.SendQuit();
            fixture.RequestRefresh();
        });
        await fixture.RefreshAsync();

        await fixture.AssertQuitCompletedAsync();
        Assert.AreEqual(1, fixture.ArtworkCollectionReads);
        Assert.AreEqual(0, fixture.ArtworkExports);
    }

    [TestMethod]
    [DataRow(8, "Artwork.Item")]
    [DataRow(9, "Artwork.Save")]
    public async Task QuitDuringRetryReleasesArtworkWithoutPublishingIt(int eventId, string member)
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await ITunesNativeFixture.CreateAsync(eventId, timeProvider: clock);
        fixture.HasArtwork = false;
        await fixture.RefreshAsync();
        fixture.HasArtwork = true;
        fixture.QuitDuring = member;
        clock.Advance(TimeSpan.FromSeconds(1));

        await fixture.RefreshAsync();

        await fixture.AssertQuitCompletedAsync();
        Assert.AreEqual(2, fixture.ArtworkCollectionReads);
    }

    [TestMethod]
    public async Task DisposalDropsAnAlreadyQueuedArtworkRetry()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await ITunesNativeFixture.CreateAsync(timeProvider: clock);
        fixture.HasArtwork = false;
        await fixture.RefreshAsync();
        fixture.HasArtwork = true;
        clock.Advance(TimeSpan.FromSeconds(1));

        Task? disposal = null;
        await fixture.RunAsync(() =>
        {
            fixture.RequestRefresh();
            disposal = fixture.Backend.DisposeAsync().AsTask();
        });
        Assert.IsNotNull(disposal);
        await disposal.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.ThrowsExactly<ObjectDisposedException>(fixture.RequestRefresh);

        Assert.AreEqual(1, fixture.ArtworkCollectionReads);
        Assert.AreEqual(0, fixture.ArtworkExports);
        fixture.AssertTemporaryObjectsReleased();
    }

    [TestMethod]
    public async Task FixtureDisposalFreesNativeObjectsWhenArtworkCleanupFails()
    {
        var fixture = await ITunesNativeFixture.CreateAsync();
        try
        {
            await fixture.RefreshAsync();
            fixture.AssertArtworkFilesDeleted();
            var path = fixture.ExportedArtworkFiles.Single();
            // Recreate the exported path with a lock the fixture does not own.
            using var blocker = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

            await Assert.ThrowsExactlyAsync<IOException>(() => fixture.DisposeAsync().AsTask());

            Assert.IsTrue(fixture.AppReleased.IsCompletedSuccessfully);
            fixture.AssertTemporaryObjectsReleased();
            Assert.IsTrue(fixture.AllNativeObjectsDisposed);
            Assert.IsTrue(File.Exists(path));
        }
        finally
        {
            if (fixture.AllNativeObjectsDisposed)
            {
                fixture.ReleaseArtworkFileLocks();
            }
            else
            {
                await fixture.DisposeAsync();
            }
        }

        fixture.AssertArtworkFilesDeleted();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref this._timestamp);

        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref this._timestamp, elapsed.Ticks);
    }
}