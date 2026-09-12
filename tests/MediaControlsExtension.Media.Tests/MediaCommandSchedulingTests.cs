// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class MediaCommandSchedulingTests
{
    [TestMethod]
    public async Task BusyProviderDoesNotDelayAnotherProvider()
    {
        var slow = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Slow"));
        var healthy = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Healthy"));
        slow.BlockCommands();
        await using var composite = new CompositeMediaBackend(Registry(slow, healthy));
        await using var service = new MediaService(composite);
        await service.StartAsync();
        await WaitUntilAsync(() => service.Sessions.Length == 2);
        try
        {
            var active = Submit(service, "Slow", MediaOperation.SkipNext);
            await slow.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
            var independent = Submit(service, "Healthy", MediaOperation.Pause);

            Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await independent.Completion!.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.IsFalse(active.Completion!.IsCompleted);
        }
        finally
        {
            slow.ReleaseCommands();
        }
    }

    [TestMethod]
    public async Task SameProviderSessionsProgressIndependentlyAndKeepTheirOwnOrder()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, 1, (1, "Slow"), (2, "Independent")));
        backend.BlockCommands(new(new(1), 1));
        await using var service = new MediaService(backend);
        await service.StartAsync();
        try
        {
            var active = Submit(service, "Slow", MediaOperation.SkipNext);
            await backend.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = Submit(service, "Slow", MediaOperation.Pause);
            var independent = Submit(service, "Independent", MediaOperation.SkipNext);
            Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await independent.Completion!.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.IsFalse(active.Completion!.IsCompleted);
            Assert.IsFalse(pending.Completion!.IsCompleted);

            backend.ReleaseCommands();
            await Task.WhenAll(active.Completion!, pending.Completion!).WaitAsync(TimeSpan.FromSeconds(5));
            var events = backend.CommandEvents.Where(static item => item.Command.SessionId.Value == 1).ToArray();
            CollectionAssert.AreEqual(
                new[] { (MediaOperation.SkipNext, false), (MediaOperation.SkipNext, true), (MediaOperation.Pause, false), (MediaOperation.Pause, true) },
                events.Select(static item => (item.Command.Operation, item.Completed)).ToArray());
        }
        finally
        {
            backend.ReleaseCommands();
        }
    }

    [TestMethod]
    public async Task SharedPlayDoesNotWaitForASessionWithoutPauseSupport()
    {
        var target = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Target"));
        var unsupportedSnapshot = FakeMediaBackend.CreateSnapshot(1, "Unsupported");
        var unsupported = new FakeMediaBackend(unsupportedSnapshot with
        {
            Sessions = [unsupportedSnapshot.Sessions.Single() with { Capabilities = MediaCapabilities.SkipNext }],
        });
        unsupported.BlockCommands();
        await using var composite = new CompositeMediaBackend(Registry(target, unsupported));
        await using var service = new MediaService(composite);
        service.UpdateOptions(new(PauseOtherSessionsOnPlay: true));
        await service.StartAsync();
        await WaitUntilAsync(() => service.Sessions.Length == 2);
        try
        {
            var active = Submit(service, "Unsupported", MediaOperation.SkipNext);
            await unsupported.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
            var play = Submit(service, "Target", MediaOperation.Play);
            Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await play.Completion!.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.IsFalse(active.Completion!.IsCompleted);
            Assert.AreEqual(1, unsupported.Commands.Length);
        }
        finally
        {
            unsupported.ReleaseCommands();
        }
    }

    [TestMethod]
    public async Task OverlappingPlayOperationsRunInAdmissionOrder()
    {
        var first = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "First"));
        var second = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Second"));
        second.BlockCommands();
        await using var composite = new CompositeMediaBackend(Registry(first, second));
        await using var service = new MediaService(composite);
        service.UpdateOptions(new(PauseOtherSessionsOnPlay: true));
        await service.StartAsync();
        await WaitUntilAsync(() => service.Sessions.Length == 2);
        try
        {
            var playFirst = Submit(service, "First", MediaOperation.Play);
            await second.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
            var playSecond = Submit(service, "Second", MediaOperation.Play);
            second.ReleaseCommands();

            var outcomes = await Task.WhenAll(playFirst.Completion!, playSecond.Completion!).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(outcomes.All(static result => result.Status == MediaCommandOutcomeStatus.Completed));
            CollectionAssert.AreEqual(new[] { MediaOperation.Play, MediaOperation.Pause }, first.Commands.Select(static command => command.Operation).ToArray());
            CollectionAssert.AreEqual(new[] { MediaOperation.Pause, MediaOperation.Play }, second.Commands.Select(static command => command.Operation).ToArray());
        }
        finally
        {
            second.ReleaseCommands();
        }
    }

    [TestMethod]
    public async Task DisposalCancelsEveryCommandAndDrainsActiveCalls()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, 1, (1, "First"), (2, "Second")))
        {
            IgnoreCommandCancellation = true,
        };
        backend.BlockCommands();
        await using var service = new MediaService(backend);
        await service.StartAsync();
        try
        {
            var first = Submit(service, "First", MediaOperation.SkipNext);
            var second = Submit(service, "Second", MediaOperation.SkipNext);
            await WaitUntilAsync(() => backend.Commands.Length == 2);
            var firstPending = Submit(service, "First", MediaOperation.Pause);
            var secondPending = Submit(service, "Second", MediaOperation.Pause);
            var disposal = service.DisposeAsync().AsTask();

            var outcomes = await Task.WhenAll(first.Completion!, second.Completion!, firstPending.Completion!, secondPending.Completion!)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(outcomes.All(static result => result.Status == MediaCommandOutcomeStatus.Canceled));
            Assert.IsFalse(disposal.IsCompleted);
            Assert.AreEqual(0, backend.DisposeCount);
            backend.ReleaseCommands();
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(2, backend.Commands.Length);
            Assert.AreEqual(1, backend.DisposeCount);
            Assert.AreEqual(MediaServiceStatus.Stopped, service.Status);
        }
        finally
        {
            backend.ReleaseCommands();
        }
    }

    [TestMethod]
    public async Task TimedOutCallKeepsOnlyItsBindingBusyUntilItFinishes()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, 1, (1, "Slow"), (2, "Independent")))
        {
            IgnoreCommandCancellation = true,
        };
        backend.BlockCommands(new(new(1), 1));
        await using var composite = new CompositeMediaBackend(Registry(backend), operationTimeout: TimeSpan.FromMilliseconds(100));
        await composite.StartAsync(default);
        var snapshot = await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 2);
        var slow = snapshot.Sessions.Single(static session => session.MediaProperties.Title == "Slow");
        var independent = snapshot.Sessions.Single(static session => session.MediaProperties.Title == "Independent");
        try
        {
            var timedOut = await composite.ExecuteAsync(new(slow.Id, slow.BindingGeneration, MediaOperation.SkipNext, []), default);
            Assert.AreEqual(MediaBackendCommandStatus.Unavailable, timedOut.Status);
            var busy = await composite.ExecuteAsync(new(slow.Id, slow.BindingGeneration, MediaOperation.Pause, []), default);
            Assert.AreEqual(MediaBackendCommandStatus.Unavailable, busy.Status);
            Assert.AreEqual(1, backend.Commands.Length);
            Assert.AreEqual(MediaBackendCommandStatus.Completed,
                (await composite.ExecuteAsync(new(independent.Id, independent.BindingGeneration, MediaOperation.Pause, []), default)).Status);

            var rebound = FakeMediaBackend.CreateSnapshot(2, 1, (1, "Replacement"), (2, "Independent"));
            backend.SetSnapshot(rebound with
            {
                Sessions = [.. rebound.Sessions.Select(static session => session with
                {
                    BindingGeneration = session.Id.Value == 1 ? 2 : 1,
                })],
            });
            await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Any(static session => session.BindingGeneration == 2));
            Assert.AreEqual(MediaBackendCommandStatus.Completed,
                (await composite.ExecuteAsync(new(slow.Id, 2, MediaOperation.Pause, []), default)).Status);
        }
        finally
        {
            backend.ReleaseCommands();
        }
    }

    private static MediaCommandSubmission Submit(MediaService service, string title, MediaOperation operation)
    {
        var session = service.Sessions.Single(session => session.MediaProperties.Title == title);
        var submission = service.TrySubmit(new(MediaCommandTarget.ForSession(session.Id), operation));
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, submission.Status);
        return submission;
    }

    private static MediaBackendRegistry Registry(params FakeMediaBackend[] backends)
    {
        var registry = new MediaBackendRegistry();
        for (var index = 0; index < backends.Length; index++)
        {
            var backend = backends[index];
            registry.Register(new($"provider{index}", $"Provider {index}", "Test provider", _ => backend, EnabledByDefault: true));
        }

        return registry;
    }

    private static async Task<MediaBackendSnapshot> WaitForSnapshotAsync(CompositeMediaBackend composite, Func<MediaBackendSnapshot, bool> predicate)
    {
        MediaBackendSnapshot? snapshot = null;
        await WaitUntilAsync(async () =>
        {
            snapshot = await composite.ReadSnapshotAsync(default);
            return predicate(snapshot);
        });
        return snapshot!;
    }

    private static Task WaitUntilAsync(Func<bool> predicate) => WaitUntilAsync(() => Task.FromResult(predicate()));

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}