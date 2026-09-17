// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.State;
using JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class RemoteSessionTests
{
    [TestMethod]
    public async Task ChangingConnectionWithoutRebindingRejectsTheSnapshotAndRecoversWithANewGeneration()
    {
        var snapshot = FakeMediaBackend.CreateSnapshot(1, "Player");
        var backend = new FakeMediaBackend(snapshot);
        var registry = new MediaBackendRegistry().Register(new("provider", "Provider", "", _ => backend, true));
        await using var service = new MediaService(new CompositeMediaBackend(registry));
        await service.StartAsync();
        await WaitUntilAsync(() => service.Sessions.Length == 1);
        var session = service.Sessions.Single();
        snapshot = snapshot with
        {
            Revision = 2,
            Sessions = [snapshot.Sessions[0] with { Origin = new("replacement", false) }],
        };
        backend.SetSnapshot(snapshot);
        await WaitUntilAsync(() => !session.IsAvailable);
        Assert.AreEqual("local", session.Origin.ConnectionId);
        Assert.AreNotEqual(MediaCommandSubmissionStatus.Accepted,
            service.TrySubmit(new(MediaCommandTarget.ForSession(session.Id), MediaOperation.Play)).Status);
        Assert.IsEmpty(backend.Commands);

        backend.SetSnapshot(snapshot with { Revision = 3, Sessions = [snapshot.Sessions[0] with { BindingGeneration = 2 }] });
        await WaitUntilAsync(() => session.IsAvailable && session.Origin.ConnectionId == "replacement");
        Assert.AreSame(session, service.Sessions.Single());
        Assert.IsNull(service.CurrentSession);
    }

    [TestMethod]
    [DataRow(true, true, false, true)]
    [DataRow(true, false, false, false)]
    [DataRow(false, true, false, false)]
    [DataRow(false, false, false, false)]
    [DataRow(true, true, true, true)]
    [DataRow(true, false, true, true)]
    [DataRow(false, true, true, true)]
    [DataRow(false, false, true, true)]
    public void AutomaticPausingRequiresBothParticipants(bool primaryLocal, bool secondaryLocal, bool includeRemote, bool shouldPause)
    {
        var snapshot = FakeMediaBackend.CreateSnapshot(1, 1, (1, "Primary"), (2, "Secondary"));
        snapshot = snapshot with
        {
            Sessions = [snapshot.Sessions[0] with { Origin = new("first", primaryLocal) },
                snapshot.Sessions[1] with { Origin = new("second", secondaryLocal), PlaybackState = MediaPlaybackState.Playing }],
        };
        var store = new MediaStateStore();
        store.ApplyBackendSnapshot(snapshot);
        store.UpdateOptions(new(true, includeRemote));
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted,
            store.TryResolveCommand(new(MediaCommandTarget.ForSession(new(1)), MediaOperation.Play), out var command));
        Assert.AreEqual(shouldPause ? 1 : 0, command.SessionsToPause.Length);
        if (shouldPause)
        {
            Assert.AreEqual(new MediaBackendSessionTarget(new(2), 1), command.SessionsToPause.Single());
        }

        store.UpdateOptions(new(false, includeRemote));
        store.TryResolveCommand(new(MediaCommandTarget.ForSession(new(1)), MediaOperation.Play), out command);
        Assert.IsEmpty(command.SessionsToPause);
    }

    [TestMethod]
    public void RemoteSessionsRequireExplicitSelectionAndDoNotStealItBack()
    {
        var snapshot = FakeMediaBackend.CreateSnapshot(1, "Remote", playbackState: MediaPlaybackState.Playing);
        snapshot = snapshot with { Sessions = [snapshot.Sessions[0] with { Origin = new("remote", false) }] };
        var store = new MediaStateStore();
        Assert.IsNull(store.ApplyBackendSnapshot(snapshot).CurrentSessionId);
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted,
            store.TryResolveCommand(new(MediaCommandTarget.ForSession(new(1)), MediaOperation.Play), out var command));
        store.ApplyAcceptedCommand(command, new(1));
        Assert.AreEqual(new MediaSessionId(1), store.ApplyBackendSnapshot(snapshot with { Revision = 2 }).CurrentSessionId);
    }

    [TestMethod]
    [DataRow(MediaOperation.SwitchNextSession)]
    [DataRow(MediaOperation.SwitchPreviousSession)]
    public void AutomaticSelectionAndCyclingSkipRemoteSessions(MediaOperation operation)
    {
        var snapshot = FakeMediaBackend.CreateSnapshot(1, 2, (1, "Local"), (2, "Remote"), (3, "Other local"));
        snapshot = snapshot with
        {
            Sessions = [snapshot.Sessions[0], snapshot.Sessions[1] with
                { Origin = new("remote", false), PlaybackState = MediaPlaybackState.Playing }, snapshot.Sessions[2]],
        };
        var store = new MediaStateStore();
        Assert.AreEqual(new MediaSessionId(1), store.ApplyBackendSnapshot(snapshot).CurrentSessionId);
        store.UpdateOptions(new(true, true));
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted,
            store.TryResolveCommand(new(MediaCommandTarget.CurrentSession, operation), out var command));
        Assert.AreEqual(new MediaSessionId(3), command.SessionId);
    }

    [TestMethod]
    public void TreatmentChangesPublishRegroupingWithoutReplacingSessionObjects()
    {
        var store = new MediaStateStore();
        var catalog = new MediaSessionCatalog();
        var snapshot = FakeMediaBackend.CreateSnapshot(1, "VLC");
        snapshot = snapshot with { Sessions = [snapshot.Sessions[0] with { Origin = new("http") }] };
        catalog.Apply(store.ApplyBackendSnapshot(snapshot));
        var session = catalog.State.Sessions.Single();
        snapshot = snapshot with
        {
            Revision = 2,
            Sessions = [snapshot.Sessions[0] with { Origin = new("http", false) }],
        };
        var publication = catalog.Apply(store.ApplyBackendSnapshot(snapshot));
        Assert.AreSame(session, catalog.State.Sessions.Single());
        Assert.IsFalse(session.Origin.TreatAsLocal);
        Assert.AreEqual("http", session.Origin.ConnectionId);
        Assert.IsTrue(publication.ServiceChanges.HasFlag(MediaServiceChanges.Sessions));
        Assert.IsTrue(publication.SessionNotifications.Single().Changes.HasFlag(MediaSessionChanges.Origin));
        Assert.IsNull(catalog.State.CurrentSession);
        Assert.IsEmpty(catalog.Apply(store.ApplyBackendSnapshot(snapshot with { Revision = 3 })).SessionNotifications);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}