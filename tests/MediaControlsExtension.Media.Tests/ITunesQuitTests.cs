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
public sealed class ITunesQuitTests
{
    public static IEnumerable<object[]> RefreshQuitCases
    {
        get
        {
            foreach (var eventId in new[] { 8, 9 })
            {
                foreach (var member in new[]
                {
                    "PlayerState", "CurrentPlaylist", "Playlist.Repeat", "CurrentTrack", "Track.Name",
                    "Track.DatabaseId", "Track.Artwork", "Artwork.Item", "Artwork.Format", "Artwork.Save", "PlayerPosition",
                })
                {
                    yield return [eventId, member];
                }
            }
        }
    }

    [TestMethod]
    [DataRow(8, true)]
    [DataRow(9, true)]
    [DataRow(8, false)]
    [DataRow(9, false)]
    public async Task QuitRejectsCommandsQueuedBeforeOrSubmittedAfterNotification(int eventId, bool queueBeforeQuit)
    {
        await using var fixture = await ITunesNativeFixture.CreateAsync(eventId);
        var commands = new List<Task<MediaBackendCommandResult>>();
        await fixture.RunAsync(() =>
        {
            if (!queueBeforeQuit)
            {
                fixture.SendQuit();
            }

            foreach (var operation in new[]
            {
                MediaOperation.Play, MediaOperation.Pause, MediaOperation.Stop, MediaOperation.SkipNext,
                MediaOperation.SkipPrevious, MediaOperation.ToggleShuffle, MediaOperation.ToggleRepeat,
            })
            {
                commands.Add(fixture.ExecuteAsync(operation));
            }

            if (queueBeforeQuit)
            {
                fixture.SendQuit();
            }
        });

        foreach (var result in await Task.WhenAll(commands))
        {
            Assert.AreEqual(MediaBackendCommandStatus.Unavailable, result.Status);
        }

        await fixture.AssertQuitCompletedAsync();
    }

    [TestMethod]
    [DataRow(8)]
    [DataRow(9)]
    public async Task QuitDropsAnAlreadyQueuedRefresh(int eventId)
    {
        await using var fixture = await ITunesNativeFixture.CreateAsync(eventId);
        await fixture.RunAsync(() =>
        {
            fixture.RequestRefresh();
            fixture.SendQuit();
        });

        await fixture.AssertQuitCompletedAsync();
    }

    [TestMethod]
    [DynamicData(nameof(RefreshQuitCases))]
    public async Task QuitDuringRefreshUnwindsOwnedObjectsWithoutFurtherCalls(int eventId, string member)
    {
        await using var fixture = await ITunesNativeFixture.CreateAsync(eventId);
        fixture.QuitDuring = member;
        fixture.RequestRefresh();

        await fixture.AssertQuitCompletedAsync();
    }

    [TestMethod]
    [DataRow(8, MediaOperation.Play, "Play")]
    [DataRow(9, MediaOperation.Play, "Play")]
    [DataRow(8, MediaOperation.Play, "PlayerState")]
    [DataRow(9, MediaOperation.Play, "PlayerState")]
    [DataRow(8, MediaOperation.Play, "PlayerPosition")]
    [DataRow(9, MediaOperation.Play, "PlayerPosition")]
    [DataRow(8, MediaOperation.ToggleShuffle, "CurrentPlaylist")]
    [DataRow(9, MediaOperation.ToggleShuffle, "CurrentPlaylist")]
    [DataRow(8, MediaOperation.ToggleShuffle, "Playlist.Shuffle")]
    [DataRow(9, MediaOperation.ToggleShuffle, "Playlist.Shuffle")]
    [DataRow(8, MediaOperation.ToggleShuffle, "Playlist.SetShuffle")]
    [DataRow(9, MediaOperation.ToggleShuffle, "Playlist.SetShuffle")]
    [DataRow(8, MediaOperation.ToggleRepeat, "Playlist.Repeat")]
    [DataRow(9, MediaOperation.ToggleRepeat, "Playlist.Repeat")]
    [DataRow(8, MediaOperation.ToggleRepeat, "Playlist.SetRepeat")]
    [DataRow(9, MediaOperation.ToggleRepeat, "Playlist.SetRepeat")]
    public async Task QuitDuringCommandStopsRemainingCallsAndReturnsUnavailable(int eventId, MediaOperation operation, string member)
    {
        await using var fixture = await ITunesNativeFixture.CreateAsync(eventId);
        fixture.QuitDuring = member;

        var result = await fixture.ExecuteAsync(operation);

        Assert.AreEqual(MediaBackendCommandStatus.Unavailable, result.Status);
        await fixture.AssertQuitCompletedAsync();
    }

    [TestMethod]
    [DataRow(8)]
    [DataRow(9)]
    public async Task QuitDuringFailedReadLeavesCleanupToTheQuitHandler(int eventId)
    {
        await using var fixture = await ITunesNativeFixture.CreateAsync(eventId);
        fixture.QuitDuring = "PlayerState";
        fixture.StateReadResult = unchecked((int)0x80010108);
        fixture.RequestRefresh();

        await fixture.AssertQuitCompletedAsync();
        Assert.AreEqual(Environment.ProcessId, fixture.GetBackendField("_quittingProcessId"));
    }

    [TestMethod]
    [DataRow(8, 0)]
    [DataRow(9, 0)]
    [DataRow(8, unchecked((int)0x80010108))]
    [DataRow(9, unchecked((int)0x80010108))]
    public async Task QuitDuringMissingTrackReadLeavesSessionRemovalToTheQuitHandler(int eventId, int result)
    {
        await using var fixture = await ITunesNativeFixture.CreateAsync(eventId);
        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await fixture.ExecuteAsync(MediaOperation.Play)).Status);
        var before = await fixture.Backend.ReadSnapshotAsync(default);
        Assert.HasCount(1, before.Sessions);
        fixture.QuitDuring = "CurrentTrack";
        fixture.HasCurrentTrack = false;
        fixture.TrackReadResult = result;
        fixture.RequestRefresh();

        await fixture.AssertQuitCompletedAsync();
        var after = await fixture.Backend.ReadSnapshotAsync(default);
        Assert.AreEqual(before.Revision + 1, after.Revision, "Quit should publish only the final disconnected state.");
    }

    [TestMethod]
    public async Task NormalCommandsAndRefreshStillPublishMediaAndReleaseTemporaryObjects()
    {
        await using var fixture = await ITunesNativeFixture.CreateAsync(9);
        foreach (var operation in new[] { MediaOperation.Play, MediaOperation.ToggleShuffle, MediaOperation.ToggleRepeat })
        {
            var result = await fixture.ExecuteAsync(operation);
            Assert.AreEqual(MediaBackendCommandStatus.Completed, result.Status);
        }

        var snapshot = await fixture.Backend.ReadSnapshotAsync(default);
        Assert.AreEqual(MediaBackendConnectionState.Connected, snapshot.Connection);
        Assert.AreEqual("Track", snapshot.Sessions.Single().MediaProperties.Title);
        Assert.IsNotNull(snapshot.Sessions.Single().MediaProperties.Artwork);
        Assert.IsTrue(fixture.ShuffleEnabled);
        Assert.AreEqual((int)ITPlaylistRepeatMode.All, fixture.RepeatMode);
        fixture.AssertTemporaryObjectsReleased();
    }

    [TestMethod]
    [DataRow(8)]
    [DataRow(9)]
    public async Task QuitWaitsForProcessExitWithoutResumingDiscovery(int eventId)
    {
        await using var fixture = await ITunesNativeFixture.CreateAsync(eventId);
        await fixture.RunAsync(fixture.SendQuit);
        await fixture.AssertQuitCompletedAsync();
        var before = await fixture.Backend.ReadSnapshotAsync(default);
        var discoveryChecks = fixture.DiscoveryChecks;

        await fixture.RunAsync(() => fixture.InvokeBackend("DiscoverITunes"));
        fixture.RequestRefresh();
        await fixture.RunAsync(fixture.SendQuit);

        Assert.AreEqual(discoveryChecks, fixture.DiscoveryChecks);
        Assert.AreEqual(false, fixture.GetBackendField("_isDiscoveryTimerRunning"));
        Assert.AreEqual(before.Revision, (await fixture.Backend.ReadSnapshotAsync(default)).Revision);
        Assert.AreEqual(MediaBackendCommandStatus.Unavailable, (await fixture.ExecuteAsync(MediaOperation.Play)).Status);
    }

    [TestMethod]
    [DataRow(8)]
    [DataRow(9)]
    public async Task ProcessExitAfterQuitReleasesTheWatchAndResumesDiscovery(int eventId)
    {
        await using var fixture = await ITunesNativeFixture.CreateAsync(eventId);
        await fixture.RunAsync(fixture.SendQuit);
        await fixture.AssertQuitCompletedAsync();
        var subscription = (ITunesProcessExitSubscription)fixture.GetBackendField("_processExitSubscription")!;
        await fixture.RunAsync(() => fixture.InvokeBackend("HandleConnectedProcessExited", -1));
        Assert.AreEqual(false, fixture.GetBackendField("_isDiscoveryTimerRunning"));
        Assert.IsTrue(subscription.IsAttached);

        await fixture.RunAsync(() => fixture.InvokeBackend("HandleConnectedProcessExited", Environment.ProcessId));

        Assert.IsFalse(subscription.IsAttached);
        Assert.IsNull(fixture.GetBackendField("_processExitSubscription"));
        Assert.IsNull(fixture.GetBackendField("_quittingProcessId"));
        Assert.AreEqual(true, fixture.GetBackendField("_isDiscoveryTimerRunning"));
        var discoveryChecks = fixture.DiscoveryChecks;
        await fixture.RunAsync(() => fixture.InvokeBackend("DiscoverITunes"));
        Assert.IsTrue(fixture.DiscoveryChecks > discoveryChecks);
    }

    [TestMethod]
    public async Task DisposingAfterQuitReleasesTheProcessWatch()
    {
        await using var fixture = await ITunesNativeFixture.CreateAsync(9);
        await fixture.RunAsync(fixture.SendQuit);
        await fixture.AssertQuitCompletedAsync();
        var subscription = (ITunesProcessExitSubscription)fixture.GetBackendField("_processExitSubscription")!;

        await fixture.Backend.DisposeAsync();

        Assert.IsFalse(subscription.IsAttached);
        Assert.IsNull(fixture.GetBackendField("_processExitSubscription"));
        Assert.IsNull(fixture.GetBackendField("_quittingProcessId"));
        Assert.AreEqual(false, fixture.GetBackendField("_isDiscoveryTimerRunning"));
    }
}