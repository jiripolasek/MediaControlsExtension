// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Diagnostics;
using JPSoftworks.MediaControlsExtension.Media.Gsmtc;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class CompositeMediaBackendTests
{
    [TestMethod]
    public void RegistryRejectsDuplicateAndInvalidRegistrations()
    {
        var registry = new MediaBackendRegistry().Register(Registration("player", () => NewBackend("Player")));
        Assert.ThrowsExactly<ArgumentException>(() => registry.Register(Registration("player", () => NewBackend("Duplicate"))));
        Assert.ThrowsExactly<ArgumentException>(() => registry.Register(Registration(" ", () => NewBackend("Invalid"))));
        Assert.ThrowsExactly<ArgumentException>(() => new CompositeMediaBackend(registry, ["unknown"]));
    }

    [TestMethod]
    public async Task DisabledProvidersAreLazyAndReenableCreatesFreshIdentity()
    {
        var instances = new List<FakeMediaBackend>();
        var registry = new MediaBackendRegistry().Register(Registration("player", () =>
        {
            var backend = NewBackend("Player");
            instances.Add(backend);
            return backend;
        }, enabled: false));
        await using var composite = new CompositeMediaBackend(registry);
        await composite.StartAsync(default);
        Assert.AreEqual(0, instances.Count);
        Assert.AreEqual(0, (await composite.ReadSnapshotAsync(default)).Sessions.Length);

        await composite.SetEnabledAsync("player", true);
        var first = (await composite.ReadSnapshotAsync(default)).Sessions.Single();
        await composite.SetEnabledAsync("player", true);
        Assert.AreEqual(1, instances.Count);

        await composite.SetEnabledAsync("player", false);
        Assert.AreEqual(1, instances[0].DisposeCount);
        Assert.AreEqual(0, (await composite.ReadSnapshotAsync(default)).Sessions.Length);
        Assert.AreEqual(MediaBackendLifecycleStatus.Disabled, composite.Backends.Single().Status);

        await composite.SetEnabledAsync("player", true);
        var second = (await composite.ReadSnapshotAsync(default)).Sessions.Single();
        Assert.AreEqual(2, instances.Count);
        Assert.AreNotEqual(first.Id, second.Id);
        var stale = await composite.ExecuteAsync(new(first.Id, first.BindingGeneration, MediaOperation.Play, []), default);
        Assert.AreEqual(MediaBackendCommandStatus.SessionGone, stale.Status);
        Assert.IsEmpty(instances[1].Commands);
    }

    [TestMethod]
    public async Task CollidingLocalIdsRouteCommandsArtworkAndInvalidationsToTheirOwners()
    {
        var first = new FakeMediaBackend(WithArtwork(FakeMediaBackend.CreateSnapshot(1, "First", bindingGeneration: 3)))
        {
            Artwork = new("image/png", new byte[] { 1 }, null),
        };
        var second = new FakeMediaBackend(WithArtwork(FakeMediaBackend.CreateSnapshot(1, "Second", bindingGeneration: 7)))
        {
            Artwork = new("image/png", new byte[] { 2 }, null),
        };
        await using var composite = new CompositeMediaBackend(Registry(first, second));
        await composite.StartAsync(default);
        var snapshot = await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 2);
        var firstSession = snapshot.Sessions.Single(static session => session.MediaProperties.Title == "First");
        var secondSession = snapshot.Sessions.Single(static session => session.MediaProperties.Title == "Second");
        Assert.AreNotEqual(firstSession.Id, secondSession.Id);
        CollectionAssert.AreEqual(new[] { firstSession.Id, secondSession.Id }, snapshot.CurrentSessionHints.ToArray());

        var result = await composite.ExecuteAsync(new(secondSession.Id, 7, MediaOperation.SkipNext, []), default);
        Assert.AreEqual(MediaBackendCommandStatus.Completed, result.Status);
        Assert.IsEmpty(first.Commands);
        Assert.AreEqual(new MediaBackendSessionId(1), second.Commands.Single().SessionId);
        Assert.AreEqual(7L, second.Commands.Single().BindingGeneration);

        var artwork = await composite.GetArtworkAsync(secondSession.MediaProperties.Artwork!.Value, default);
        Assert.AreSame(second.Artwork, artwork);
        Assert.AreEqual(new MediaArtworkKey(new(1), 1), second.ArtworkRequests.Single());
        Assert.IsEmpty(first.ArtworkRequests);
        Assert.AreNotEqual(firstSession.MediaProperties.Artwork, secondSession.MediaProperties.Artwork);

        composite.InvalidateObservations([new(secondSession.Id, MediaBackendObservationChanges.Playback)]);
        Assert.IsEmpty(first.ObservationInvalidations);
        Assert.AreEqual(new MediaBackendSessionId(1), second.ObservationInvalidations.Single().Single().SessionId);
    }

    [TestMethod]
    public async Task RebindingInvalidatesOldArtworkAndRejectsOldCommands()
    {
        var backend = new FakeMediaBackend(WithArtwork(FakeMediaBackend.CreateSnapshot(1, "Old")));
        await using var composite = new CompositeMediaBackend(Registry(backend));
        await composite.StartAsync(default);
        var original = (await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 1)).Sessions.Single();
        backend.SetSnapshot(WithArtwork(FakeMediaBackend.CreateSnapshot(2, "New", bindingGeneration: 2)));
        var updated = (await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.FirstOrDefault()?.BindingGeneration == 2))
            .Sessions.Single();
        Assert.AreEqual(original.Id, updated.Id);
        Assert.AreNotEqual(original.MediaProperties.Artwork, updated.MediaProperties.Artwork);
        Assert.IsNull(await composite.GetArtworkAsync(original.MediaProperties.Artwork!.Value, default));
        Assert.IsEmpty(backend.ArtworkRequests);
        Assert.AreEqual(MediaBackendCommandStatus.SessionGone,
            (await composite.ExecuteAsync(new(original.Id, original.BindingGeneration, MediaOperation.Play, []), default)).Status);
    }

    [TestMethod]
    public async Task QueuedPlayDoesNotPauseAReboundSecondarySession()
    {
        var blocker = NewBackend("Blocker");
        var target = NewBackend("Target");
        var other = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Other", bindingGeneration: 7));
        blocker.BlockCommands();
        await using var composite = new CompositeMediaBackend(Registry(blocker, target, other));
        await using var service = new MediaService(composite);
        service.UpdateOptions(new(PauseOtherSessionsOnPlay: true));
        await service.StartAsync();
        await WaitUntilAsync(() => Task.FromResult(service.Sessions.Length == 3));
        var blockerSession = service.Sessions.Single(static session => session.MediaProperties.Title == "Blocker");
        var targetSession = service.Sessions.Single(static session => session.MediaProperties.Title == "Target");
        try
        {
            var active = service.TrySubmit(new(MediaCommandTarget.ForSession(blockerSession.Id), MediaOperation.SkipNext));
            Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, active.Status);
            await blocker.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = service.TrySubmit(new(MediaCommandTarget.ForSession(targetSession.Id), MediaOperation.Play));
            Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, pending.Status);

            other.SetSnapshot(FakeMediaBackend.CreateSnapshot(2, "Replacement", bindingGeneration: 8));
            await WaitUntilAsync(() => Task.FromResult(service.Sessions.Any(static session => session.MediaProperties.Title == "Replacement")));
            blocker.ReleaseCommands();

            Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await active.Completion!.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await pending.Completion!.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.AreEqual(MediaOperation.Pause, blocker.Commands.Last().Operation);
            Assert.IsEmpty(other.Commands);
            Assert.AreEqual(MediaOperation.Play, target.Commands.Single().Operation);
        }
        finally
        {
            blocker.ReleaseCommands();
        }
    }

    [TestMethod]
    public async Task ReboundTargetIsRejectedAfterPausingOtherProviders()
    {
        var target = NewBackend("Target");
        var other = NewBackend("Other");
        other.BlockCommands();
        await using var composite = new CompositeMediaBackend(Registry(target, other));
        await composite.StartAsync(default);
        var snapshot = await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 2);
        var targetSession = snapshot.Sessions.Single(static session => session.MediaProperties.Title == "Target");
        var otherSession = snapshot.Sessions.Single(static session => session.MediaProperties.Title == "Other");
        try
        {
            var command = composite.ExecuteAsync(new(targetSession.Id, targetSession.BindingGeneration, MediaOperation.Play,
                [new(otherSession.Id, otherSession.BindingGeneration)]), default);
            await other.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
            target.SetSnapshot(FakeMediaBackend.CreateSnapshot(2, "Replacement", bindingGeneration: 2));
            await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Any(static session => session.BindingGeneration == 2));
            other.ReleaseCommands();

            var result = await command.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(MediaBackendCommandStatus.SessionGone, result.Status);
            Assert.AreEqual(new MediaBackendPauseResult(new(otherSession.Id, otherSession.BindingGeneration),
                MediaBackendCommandStatus.Completed, null), result.PauseResults.Single());
            Assert.IsEmpty(target.Commands);
        }
        finally
        {
            other.ReleaseCommands();
        }
    }

    [TestMethod]
    [DataRow(1L, 1L, true)]
    [DataRow(1L, 2L, false)]
    [DataRow(2L, 1L, false)]
    public async Task ArtworkCompletionIsAcceptedOnlyForTheCurrentImageAndBinding(long generation, long artworkVersion, bool isCurrent)
    {
        var backend = new FakeMediaBackend(WithArtwork(FakeMediaBackend.CreateSnapshot(1, "Original")))
        {
            Artwork = new("image/png", new byte[] { 1 }, null),
        };
        backend.BlockArtwork();
        await using var composite = new CompositeMediaBackend(Registry(backend));
        await composite.StartAsync(default);
        var original = (await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 1)).Sessions.Single();
        try
        {
            var artwork = composite.GetArtworkAsync(original.MediaProperties.Artwork!.Value, default).AsTask();
            await backend.ArtworkStarted.WaitAsync(TimeSpan.FromSeconds(5));
            backend.SetSnapshot(WithArtwork(FakeMediaBackend.CreateSnapshot(2, "Updated", bindingGeneration: generation), artworkVersion));
            await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Single().MediaProperties.Title == "Updated");
            backend.ReleaseArtwork();

            var content = await artwork.WaitAsync(TimeSpan.FromSeconds(5));
            if (isCurrent)
            {
                Assert.AreSame(backend.Artwork, content);
            }
            else
            {
                Assert.IsNull(content);
            }
        }
        finally
        {
            backend.ReleaseArtwork();
        }
    }

    [TestMethod]
    public async Task EnabledProviderCanStayEmptyAndReconnectWithoutRecreation()
    {
        var backend = new FakeMediaBackend(new(1, [], [], MediaControlAvailability.Available));
        var creates = 0;
        var registry = new MediaBackendRegistry().Register(Registration("player", () =>
        {
            Interlocked.Increment(ref creates);
            return backend;
        }));
        await using var composite = new CompositeMediaBackend(registry);
        await using var service = new MediaService(composite);
        await service.StartAsync();
        await WaitUntilAsync(() => Task.FromResult(composite.Backends.Single().Status == MediaBackendLifecycleStatus.Ready &&
            service.Status == MediaServiceStatus.Ready));
        Assert.IsEmpty(service.Sessions);

        backend.SetSnapshot(WithArtwork(FakeMediaBackend.CreateSnapshot(2, "Connected")));
        await WaitUntilAsync(() => Task.FromResult(service.Sessions.Length == 1));
        var original = service.Sessions.Single();
        backend.SetSnapshot(new(3, [], [], MediaControlAvailability.Available));
        await WaitUntilAsync(() => Task.FromResult(service.Sessions.IsEmpty));
        Assert.AreEqual(MediaServiceStatus.Ready, service.Status);

        backend.SetSnapshot(WithArtwork(FakeMediaBackend.CreateSnapshot(4, "Reconnected", sessionId: 2)));
        await WaitUntilAsync(() => Task.FromResult(service.Sessions.Length == 1));
        var replacement = service.Sessions.Single();
        Assert.AreNotEqual(original.Id, replacement.Id);
        Assert.AreEqual(MediaCommandSubmissionStatus.SessionGone,
            service.TrySubmit(new(MediaCommandTarget.ForSession(original.Id), MediaOperation.Play)).Status);
        Assert.IsNull(await composite.GetArtworkAsync(original.MediaProperties.Artwork!.Value, default));
        var command = service.TrySubmit(new(MediaCommandTarget.ForSession(replacement.Id), MediaOperation.Play));
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, command.Status);
        Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await command.Completion!.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.AreEqual(1, Volatile.Read(ref creates));
        Assert.AreEqual(0, backend.DisposeCount);
        Assert.IsTrue(composite.Backends.Single().IsEnabled);
        Assert.AreEqual(MediaBackendLifecycleStatus.Ready, composite.Backends.Single().Status);
    }

    [TestMethod]
    public async Task PlayPausesOtherProvidersAndToleratesUnsupportedOrFailedPauses()
    {
        var target = NewBackend("Target");
        var other = NewBackend("Other");
        other.CommandResult = new(MediaBackendCommandStatus.Failed, "Cannot pause");
        var unsupportedSnapshot = FakeMediaBackend.CreateSnapshot(1, "Unsupported");
        var unsupported = new FakeMediaBackend(unsupportedSnapshot with
        {
            Sessions = [unsupportedSnapshot.Sessions.Single() with { Capabilities = MediaCapabilities.Play }],
        });
        await using var composite = new CompositeMediaBackend(Registry(target, other, unsupported));
        await composite.StartAsync(default);
        var snapshot = await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 3);
        var targetSession = snapshot.Sessions.Single(static session => session.MediaProperties.Title == "Target");
        var result = await composite.ExecuteAsync(new(targetSession.Id, 1, MediaOperation.Play,
            [.. snapshot.Sessions.Select(static session => new MediaBackendSessionTarget(session.Id, session.BindingGeneration))]), default);
        Assert.AreEqual(MediaBackendCommandStatus.Completed, result.Status);
        Assert.AreEqual(MediaOperation.Play, target.Commands.Single().Operation);
        Assert.AreEqual(MediaOperation.Pause, other.Commands.Single().Operation);
        Assert.IsEmpty(target.Commands.Single().SessionsToPause);
        Assert.IsEmpty(other.Commands.Single().SessionsToPause);
        Assert.IsEmpty(unsupported.Commands);
    }

    [TestMethod]
    public async Task PlayPausesEverySessionSharingTheGsmtcControlGate()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, 1,
            (1, "Target"), (2, "First"), (3, "Second")));
        var gate = new GsmtcControlGate(NullLogger.Instance);
        var completed = new List<MediaBackendCommand>();
        backend.CommandHandler = (command, cancellationToken) => gate.RunCommandAsync(async () =>
        {
            if (command.Operation == MediaOperation.Pause)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken);
            }

            completed.Add(command);
            return new MediaBackendCommandResult(MediaBackendCommandStatus.Completed, null);
        }, command.Operation.ToString(), cancellationToken);

        await using var composite = new CompositeMediaBackend(Registry(backend));
        await composite.StartAsync(default);
        var snapshot = await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 3);
        var target = snapshot.Sessions.Single(static session => session.MediaProperties.Title == "Target");
        var first = snapshot.Sessions.Single(static session => session.MediaProperties.Title == "First");
        var second = snapshot.Sessions.Single(static session => session.MediaProperties.Title == "Second");
        var result = await composite.ExecuteAsync(new(target.Id, target.BindingGeneration, MediaOperation.Play,
            [new(first.Id, first.BindingGeneration), new(second.Id, second.BindingGeneration),
                new(first.Id, first.BindingGeneration), new(target.Id, target.BindingGeneration)]), default)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(MediaBackendCommandStatus.Completed, result.Status);
        CollectionAssert.AreEqual(
            new[] { (2L, MediaOperation.Pause), (3L, MediaOperation.Pause), (1L, MediaOperation.Play) },
            completed.Select(static command => (command.SessionId.Value, command.Operation)).ToArray());
    }

    [TestMethod]
    public async Task PlayPausesIndependentProvidersWhileAnotherProviderIsBlocked()
    {
        var target = NewBackend("Target");
        var slow = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, 1, (1, "First"), (2, "Second")));
        var independent = NewBackend("Independent");
        slow.BlockCommands(new(new(1), 1));
        await using var composite = new CompositeMediaBackend(Registry(target, slow, independent));
        await composite.StartAsync(default);
        var snapshot = await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 4);
        var targetSession = snapshot.Sessions.Single(static session => session.MediaProperties.Title == "Target");
        try
        {
            var play = composite.ExecuteAsync(new(targetSession.Id, targetSession.BindingGeneration, MediaOperation.Play,
                [.. snapshot.Sessions.Select(static session => new MediaBackendSessionTarget(session.Id, session.BindingGeneration))]), default);
            await slow.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
            await independent.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(MediaOperation.Pause, independent.Commands.Single().Operation);
            Assert.IsTrue(independent.CommandEvents.Last().Completed);
            Assert.AreEqual(1, slow.Commands.Length);
            Assert.IsEmpty(target.Commands);
            Assert.IsFalse(play.IsCompleted);

            slow.ReleaseCommands();
            Assert.AreEqual(MediaBackendCommandStatus.Completed, (await play.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            long[] expectedOrder = [1, 2];
            CollectionAssert.AreEqual(expectedOrder, slow.Commands.Select(static command => command.SessionId.Value).ToArray());
            Assert.IsTrue(slow.Commands.All(static command => command.Operation == MediaOperation.Pause));
            Assert.AreEqual(MediaOperation.Play, target.Commands.Single().Operation);
        }
        finally
        {
            slow.ReleaseCommands();
        }
    }

    [TestMethod]
    public async Task PlaySkipsASecondaryBindingReplacedWhileItsProviderIsPausing()
    {
        var target = NewBackend("Target");
        var othersSnapshot = FakeMediaBackend.CreateSnapshot(1, 1, (1, "First"), (2, "Second"));
        var others = new FakeMediaBackend(othersSnapshot);
        others.BlockCommands(new(new(1), 1));
        await using var composite = new CompositeMediaBackend(Registry(target, others));
        await composite.StartAsync(default);
        var snapshot = await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 3);
        var targetSession = snapshot.Sessions.Single(static session => session.MediaProperties.Title == "Target");
        try
        {
            var play = composite.ExecuteAsync(new(targetSession.Id, targetSession.BindingGeneration, MediaOperation.Play,
                [.. snapshot.Sessions.Select(static session => new MediaBackendSessionTarget(session.Id, session.BindingGeneration))]), default);
            await others.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
            others.SetSnapshot(othersSnapshot with
            {
                Revision = 2,
                Sessions = [.. othersSnapshot.Sessions.Select(static session => session.Id.Value == 2
                    ? session with { BindingGeneration = 2 }
                    : session)],
            });
            await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Any(static session => session.BindingGeneration == 2));
            others.ReleaseCommands();

            var result = await play.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(MediaBackendCommandStatus.Completed, result.Status);
            var skipped = result.PauseResults.Single(static pause => pause.Status == MediaBackendCommandStatus.SessionGone);
            Assert.AreEqual(new MediaBackendSessionTarget(
                snapshot.Sessions.Single(static session => session.MediaProperties.Title == "Second").Id, 1), skipped.Target);
            Assert.AreEqual(1L, others.Commands.Single().SessionId.Value);
            Assert.AreEqual(MediaOperation.Play, target.Commands.Single().Operation);
        }
        finally
        {
            others.ReleaseCommands();
        }
    }

    [TestMethod]
    public async Task CancelingPlayStopsTheRemainingPausesWithinAProvider()
    {
        var target = NewBackend("Target");
        var others = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, 1, (1, "First"), (2, "Second")));
        others.BlockCommands(new(new(1), 1));
        await using var composite = new CompositeMediaBackend(Registry(target, others));
        await composite.StartAsync(default);
        var snapshot = await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 3);
        var targetSession = snapshot.Sessions.Single(static session => session.MediaProperties.Title == "Target");
        using var cancellation = new CancellationTokenSource();
        try
        {
            var play = composite.ExecuteAsync(new(targetSession.Id, targetSession.BindingGeneration, MediaOperation.Play,
                [.. snapshot.Sessions.Select(static session => new MediaBackendSessionTarget(session.Id, session.BindingGeneration))]),
                cancellation.Token);
            await others.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
            await cancellation.CancelAsync();

            await Assert.ThrowsAsync<OperationCanceledException>(() => play.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(1L, others.Commands.Single().SessionId.Value);
            Assert.IsEmpty(target.Commands);
        }
        finally
        {
            others.ReleaseCommands();
        }
    }

    [TestMethod]
    public async Task DisableDuringStartupCancelsAndSerializesReenableWithoutBlockingOtherProviders()
    {
        var first = NewBackend("Old");
        first.BlockStart();
        var second = NewBackend("New");
        var healthy = NewBackend("Healthy");
        var starts = 0;
        var registry = new MediaBackendRegistry()
            .Register(Registration("slow", () => Interlocked.Increment(ref starts) == 1 ? first : second))
            .Register(Registration("healthy", () => healthy));
        await using var composite = new CompositeMediaBackend(registry);
        await composite.StartAsync(default);
        await first.StartStarted.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var disable = composite.SetEnabledAsync("slow", false);
            var enable = composite.SetEnabledAsync("slow", true);
            await composite.SetEnabledAsync("healthy", true).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, Volatile.Read(ref starts));
            Assert.AreEqual("Healthy", (await composite.ReadSnapshotAsync(default)).Sessions.Single().MediaProperties.Title);
            Assert.AreEqual(0, first.DisposeCount);
            first.ReleaseStart();
            await Task.WhenAll(disable, enable).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(2, Volatile.Read(ref starts));
            Assert.AreEqual(1, first.DisposeCount);
            Assert.AreEqual(0, second.DisposeCount);
            Assert.IsTrue(composite.Backends.Single(static backend => backend.Id == "slow").IsEnabled);
            Assert.IsFalse((await composite.ReadSnapshotAsync(default)).Sessions.Any(static session => session.MediaProperties.Title == "Old"));
        }
        finally
        {
            first.ReleaseStart();
        }
    }

    [TestMethod]
    public async Task DisableWithdrawsSessionsImmediatelyAndDrainsLateCommandsBeforeDisposal()
    {
        var backend = NewBackend("Player");
        backend.IgnoreCommandCancellation = true;
        backend.BlockCommands();
        var healthy = NewBackend("Healthy");
        await using var composite = new CompositeMediaBackend(Registry(backend, healthy));
        await composite.StartAsync(default);
        var snapshot = await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 2);
        var session = snapshot.Sessions.Single(static session => session.MediaProperties.Title == "Player");
        var command = composite.ExecuteAsync(new(session.Id, 1, MediaOperation.Play, []), default);
        await backend.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var disable = composite.SetEnabledAsync("provider0", false);
            Assert.AreEqual("Healthy", (await composite.ReadSnapshotAsync(default)).Sessions.Single().MediaProperties.Title);
            Assert.AreEqual(MediaBackendCommandStatus.Unavailable, (await command.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.IsFalse(disable.IsCompleted);
            Assert.AreEqual(0, backend.DisposeCount);
            backend.ReleaseCommands();
            await disable.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, backend.DisposeCount);
            Assert.AreEqual(0, healthy.DisposeCount);
        }
        finally
        {
            backend.ReleaseCommands();
        }
    }

    [TestMethod]
    public async Task LateSnapshotAfterDisableCannotRestoreRemovedSessions()
    {
        var backend = NewBackend("Late");
        backend.IgnoreSnapshotCancellation = true;
        backend.BlockSnapshotReads();
        var healthy = NewBackend("Healthy");
        await using var composite = new CompositeMediaBackend(Registry(backend, healthy));
        await composite.StartAsync(default);
        await backend.SnapshotReadStarted.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var disable = composite.SetEnabledAsync("provider0", false);
            await composite.SetEnabledAsync("provider1", true).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual("Healthy", (await composite.ReadSnapshotAsync(default)).Sessions.Single().MediaProperties.Title);
            backend.ReleaseSnapshotReads();
            await disable.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual("Healthy", (await composite.ReadSnapshotAsync(default)).Sessions.Single().MediaProperties.Title);
            Assert.AreEqual(1, backend.DisposeCount);
        }
        finally
        {
            backend.ReleaseSnapshotReads();
        }
    }

    [TestMethod]
    public async Task SlowRefreshDoesNotBlockOtherProvidersOrDiscardTheirUpdates()
    {
        var slow = NewBackend("Slow");
        var healthy = NewBackend("Healthy");
        await using var composite = new CompositeMediaBackend(Registry(slow, healthy));
        await composite.StartAsync(default);
        await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 2);
        slow.BlockSnapshotReads();
        slow.SetSnapshot(FakeMediaBackend.CreateSnapshot(2, "Slow update"));
        try
        {
            await WaitUntilAsync(async () =>
            {
                await composite.ReadSnapshotAsync(default);
                return slow.SnapshotReadStarted.IsCompleted;
            });
            healthy.SetSnapshot(FakeMediaBackend.CreateSnapshot(2, "Healthy update"));
            var snapshot = await WaitForSnapshotAsync(composite, static snapshot =>
                snapshot.Sessions.Any(static session => session.MediaProperties.Title == "Healthy update"));
            Assert.IsTrue(snapshot.Sessions.Any(static session => session.MediaProperties.Title == "Slow"));
            var session = snapshot.Sessions.Single(static session => session.MediaProperties.Title == "Healthy update");
            Assert.AreEqual(MediaBackendCommandStatus.Completed,
                (await composite.ExecuteAsync(new(session.Id, 1, MediaOperation.Play, []), default)).Status);
        }
        finally
        {
            slow.ReleaseSnapshotReads();
        }
    }

    [TestMethod]
    public async Task ProviderReadFailureIsIsolatedAndRecoversOnNextSignal()
    {
        var failing = NewBackend("Failing");
        var healthy = NewBackend("Healthy");
        await using var composite = new CompositeMediaBackend(Registry(failing, healthy));
        await composite.StartAsync(default);
        await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 2);
        failing.SnapshotFailure = new InvalidOperationException("Cannot read");
        failing.Signal(MediaBackendSignal.ObservationsChanged);
        var failed = await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Any(static session => !session.IsAvailable));
        Assert.AreEqual(MediaControlAvailability.Available, failed.Availability);
        Assert.IsTrue(failed.Sessions.Single(static session => session.MediaProperties.Title == "Healthy").IsAvailable);
        failing.SnapshotFailure = null;
        failing.SetSnapshot(FakeMediaBackend.CreateSnapshot(2, "Recovered"));
        var recovered = await WaitForSnapshotAsync(composite, static snapshot =>
            snapshot.Sessions.Any(static session => session.IsAvailable && session.MediaProperties.Title == "Recovered"));
        Assert.AreEqual(failed.Sessions[0].Id, recovered.Sessions[0].Id);
        Assert.AreEqual(MediaBackendLifecycleStatus.Ready, composite.Backends[0].Status);
    }

    [TestMethod]
    public async Task StartupAndWatchFailuresDoNotEndTheCompositeStream()
    {
        var failing = NewBackend("Failing");
        failing.StartFailure = new InvalidOperationException("Cannot start");
        var healthy = NewBackend("Healthy");
        await using var composite = new CompositeMediaBackend(Registry(failing, healthy));
        await using var service = new MediaService(composite);
        await service.StartAsync();
        await WaitUntilAsync(() => Task.FromResult(service.Sessions.Length == 1 && failing.DisposeCount == 1));
        Assert.AreEqual("Healthy", service.Sessions.Single().MediaProperties.Title);
        Assert.AreEqual(MediaControlAvailability.Available, service.Availability);
        healthy.CompleteWatch(new InvalidOperationException("Disconnected"));
        await WaitUntilAsync(() => Task.FromResult(composite.Backends[1].Status == MediaBackendLifecycleStatus.Faulted));
        await WaitUntilAsync(() => Task.FromResult(!service.Sessions.Single().IsAvailable));
        Assert.AreEqual(MediaServiceStatus.Degraded, service.Status);
    }

    [TestMethod]
    public async Task FailedFactoryCanBeRetriedWithoutAffectingOtherProviders()
    {
        var attempts = 0;
        var registry = new MediaBackendRegistry().Register(Registration("player", () =>
            Interlocked.Increment(ref attempts) == 1 ? throw new InvalidOperationException("Factory failed") : NewBackend("Recovered")));
        await using var composite = new CompositeMediaBackend(registry);
        await composite.StartAsync(default);
        await WaitUntilAsync(() => Task.FromResult(composite.Backends.Single().Status == MediaBackendLifecycleStatus.Faulted));
        await composite.SetEnabledAsync("player", true);
        Assert.AreEqual("Recovered", (await composite.ReadSnapshotAsync(default)).Sessions.Single().MediaProperties.Title);
    }

    [TestMethod]
    public async Task DisposalFailurePreventsOverlappingReplacementInstances()
    {
        var backend = NewBackend("Player");
        backend.FailDisposal = true;
        var creates = 0;
        var registry = new MediaBackendRegistry().Register(Registration("player", () =>
        {
            Interlocked.Increment(ref creates);
            return backend;
        }));
        await using var composite = new CompositeMediaBackend(registry);
        await composite.StartAsync(default);
        await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 1);
        await composite.SetEnabledAsync("player", false);
        await composite.SetEnabledAsync("player", true);
        Assert.AreEqual(1, Volatile.Read(ref creates));
        Assert.AreEqual(1, backend.DisposeCount);
        Assert.AreEqual(MediaBackendLifecycleStatus.Faulted, composite.Backends.Single().Status);
        Assert.AreEqual(0, (await composite.ReadSnapshotAsync(default)).Sessions.Length);
    }

    [TestMethod]
    public async Task CommandTimeoutReleasesCallerButKeepsBackendAliveUntilTheCallReturns()
    {
        var backend = NewBackend("Player");
        backend.BlockCommands();
        backend.IgnoreCommandCancellation = true;
        await using var composite = new CompositeMediaBackend(Registry(backend), operationTimeout: TimeSpan.FromMilliseconds(100));
        await composite.StartAsync(default);
        var session = (await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 1)).Sessions.Single();
        try
        {
            var result = await composite.ExecuteAsync(new(session.Id, 1, MediaOperation.Play, []), default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(MediaBackendCommandStatus.Unavailable, result.Status);
            var disable = composite.SetEnabledAsync("provider0", false);
            Assert.IsFalse(disable.IsCompleted);
            Assert.AreEqual(0, backend.DisposeCount);
            backend.ReleaseCommands();
            await disable.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, backend.DisposeCount);
        }
        finally
        {
            backend.ReleaseCommands();
        }
    }

    [TestMethod]
    public async Task ServiceKeepsHealthySessionIdentityAcrossProviderToggles()
    {
        var first = NewBackend("First");
        var second = NewBackend("Second");
        await using var composite = new CompositeMediaBackend(Registry(first, second));
        await using var service = new MediaService(composite);
        await service.StartAsync();
        await WaitUntilAsync(() => Task.FromResult(service.Sessions.Length == 2));
        var retained = service.Sessions.Single(static session => session.MediaProperties.Title == "Second");
        await composite.SetEnabledAsync("provider0", false);
        await WaitUntilAsync(() => Task.FromResult(service.Sessions.Length == 1));
        Assert.AreSame(retained, service.CurrentSession);
        var submission = service.TrySubmit(new(MediaCommandTarget.CurrentSession, MediaOperation.Play));
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, submission.Status);
        Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await submission.Completion!).Status);
    }

    [TestMethod]
    public async Task DisposalDuringStartupWaitsForInitializationAndDisposesExactlyOnce()
    {
        var backend = NewBackend("Player");
        backend.BlockStart();
        var composite = new CompositeMediaBackend(Registry(backend));
        await composite.StartAsync(default);
        await backend.StartStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = composite.DisposeAsync().AsTask();
        Assert.IsFalse(disposal.IsCompleted);
        backend.ReleaseStart();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        await composite.DisposeAsync();
        Assert.AreEqual(1, backend.DisposeCount);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => composite.SetEnabledAsync("provider0", true));
    }

    [TestMethod]
    public async Task DisablingEveryFailedProviderPublishesReadyEmptyState()
    {
        var backend = NewBackend("Failing");
        backend.StartFailure = new InvalidOperationException("Cannot start");
        await using var composite = new CompositeMediaBackend(Registry(backend));
        await using var service = new MediaService(composite);
        await service.StartAsync();
        await WaitUntilAsync(() => Task.FromResult(composite.Backends.Single().Status == MediaBackendLifecycleStatus.Faulted));
        await composite.SetEnabledAsync("provider0", false);
        await WaitUntilAsync(() => Task.FromResult(service.Status == MediaServiceStatus.Ready));
        Assert.IsEmpty(service.Sessions);
        Assert.AreEqual(MediaControlAvailability.Available, service.Availability);
    }

    [TestMethod]
    public async Task HealthyProviderContinuesPublishingAfterAnotherWatchStreamEnds()
    {
        var failing = NewBackend("Failing");
        var healthy = NewBackend("Healthy");
        await using var composite = new CompositeMediaBackend(Registry(failing, healthy));
        await using var service = new MediaService(composite);
        await service.StartAsync();
        await WaitUntilAsync(() => Task.FromResult(service.Sessions.Length == 2));
        failing.CompleteWatch();
        await WaitUntilAsync(() => Task.FromResult(composite.Backends[0].Status == MediaBackendLifecycleStatus.Faulted));
        healthy.SetSnapshot(FakeMediaBackend.CreateSnapshot(2, "Updated"));
        await WaitUntilAsync(() => Task.FromResult(service.CurrentSession?.MediaProperties.Title == "Updated"));
        Assert.AreEqual(MediaControlAvailability.Available, service.Availability);
        Assert.AreEqual(MediaServiceStatus.Ready, service.Status);
    }

    [TestMethod]
    public async Task ProviderSessionOrderingChangesPreserveSessionIdentities()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, 1, (1, "One"), (2, "Two")));
        await using var composite = new CompositeMediaBackend(Registry(backend));
        await composite.StartAsync(default);
        var original = await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 2);
        backend.SetSnapshot(FakeMediaBackend.CreateSnapshot(2, 2, (2, "Two"), (1, "One")));
        var reordered = await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.FirstOrDefault()?.MediaProperties.Title == "Two");
        Assert.AreEqual(original.Sessions[0].Id, reordered.Sessions[1].Id);
        Assert.AreEqual(original.Sessions[1].Id, reordered.Sessions[0].Id);
        Assert.AreEqual(reordered.Sessions[0].Id, reordered.CurrentSessionHints.Single());
    }

    private static FakeMediaBackend NewBackend(string title) => new(FakeMediaBackend.CreateSnapshot(1, title));

    private static MediaBackendRegistration Registration(string id, Func<IMediaBackend> create, bool enabled = true) =>
        new(id, id, string.Empty, _ => create(), enabled);

    private static MediaBackendRegistry Registry(params FakeMediaBackend[] backends)
    {
        var registry = new MediaBackendRegistry();
        for (var index = 0; index < backends.Length; index++)
        {
            var backend = backends[index];
            registry.Register(Registration($"provider{index}", () => backend));
        }

        return registry;
    }

    private static MediaBackendSnapshot WithArtwork(MediaBackendSnapshot snapshot, long version = 1) => snapshot with
    {
        Sessions = [.. snapshot.Sessions.Select(session => session with
        {
            MediaProperties = session.MediaProperties with { Artwork = new(new(session.Id.Value), version) },
        })],
    };

    private static async Task<MediaBackendSnapshot> WaitForSnapshotAsync(CompositeMediaBackend composite, Func<MediaBackendSnapshot, bool> predicate)
    {
        MediaBackendSnapshot? latest = null;
        await WaitUntilAsync(async () =>
        {
            latest = await composite.ReadSnapshotAsync(default);
            return predicate(latest);
        });
        return latest!;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!await predicate())
        {
            Assert.IsLessThan(TimeSpan.FromSeconds(5), timer.Elapsed, "Timed out waiting for a backend transition.");
            await Task.Delay(10);
        }
    }
}