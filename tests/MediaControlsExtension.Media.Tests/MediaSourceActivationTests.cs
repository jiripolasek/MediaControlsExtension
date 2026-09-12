// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class MediaSourceActivationTests
{
    [TestMethod]
    [DataRow(MediaBackendCommandStatus.Completed, MediaCommandOutcomeStatus.Completed)]
    [DataRow(MediaBackendCommandStatus.Failed, MediaCommandOutcomeStatus.Failed)]
    [DataRow(MediaBackendCommandStatus.Unavailable, MediaCommandOutcomeStatus.Unavailable)]
    [DataRow(MediaBackendCommandStatus.Unsupported, MediaCommandOutcomeStatus.Unsupported)]
    [DataRow(MediaBackendCommandStatus.SessionGone, MediaCommandOutcomeStatus.SessionGone)]
    public async Task ActivationOnlySourceRoutesToItsOwnerWithoutChangingPlaybackOrSelection(
        MediaBackendCommandStatus backendStatus, MediaCommandOutcomeStatus expectedStatus)
    {
        var native = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Native", playbackState: MediaPlaybackState.Playing));
        var browserSnapshot = Snapshot("Tab", generation: 37);
        var browser = new FakeMediaBackend(browserSnapshot with
        {
            Sessions = [browserSnapshot.Sessions[0] with { Capabilities = MediaCapabilities.ActivateSource }],
        })
        {
            CommandResult = new(backendStatus, "Activation result"),
        };
        browser.BlockCommands();
        await using var service = new MediaService(new CompositeMediaBackend(Registry(native, browser)));
        service.UpdateOptions(new(PauseOtherSessionsOnPlay: true));
        await service.StartAsync();
        await WaitUntilAsync(() => service.Sessions.Length == 2);
        var current = service.CurrentSession;
        var target = service.Sessions.Single(static session => session.MediaProperties.Title == "Tab");
        var playback = target.PlaybackInfo;
        Assert.IsNull(target.MediaProperties.Source.NativeApplication);
        Assert.AreEqual("Native", current!.MediaProperties.Title);

        try
        {
            var activation = Submit(service, MediaCommandTarget.ForSession(target.Id), MediaOperation.ActivateSource);
            await browser.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreSame(current, service.CurrentSession);
            Assert.AreEqual(playback, target.PlaybackInfo);
            browser.ReleaseCommands();
            var outcome = await activation.Completion!.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(expectedStatus, outcome.Status);
            Assert.AreEqual("Activation result", outcome.DiagnosticMessage);
            Assert.AreEqual(target.Id, outcome.SessionId);
            Assert.AreSame(current, service.CurrentSession);
            Assert.AreEqual(playback, target.PlaybackInfo);
            Assert.IsFalse(target.PlaybackInfo.IsOptimistic);
            Assert.AreEqual(0, native.Commands.Length);
            var command = browser.Commands.Single();
            Assert.AreEqual(new MediaBackendSessionId(1), command.SessionId);
            Assert.AreEqual(37L, command.BindingGeneration);
            Assert.AreEqual(MediaOperation.ActivateSource, command.Operation);
            Assert.IsTrue(command.SessionsToPause.IsEmpty);
        }
        finally
        {
            browser.ReleaseCommands();
        }
    }

    [TestMethod]
    [DataRow(false, true, MediaCommandSubmissionStatus.Unsupported)]
    [DataRow(true, false, MediaCommandSubmissionStatus.SessionGone)]
    public async Task UnsupportedOrUnavailableSourcesAreRejectedBeforeDispatch(
        bool supportsActivation, bool isAvailable, MediaCommandSubmissionStatus expectedStatus)
    {
        var snapshot = Snapshot("Target");
        var backend = new FakeMediaBackend(snapshot with
        {
            Sessions = [snapshot.Sessions[0] with
            {
                Capabilities = supportsActivation ? MediaCapabilities.ActivateSource : MediaCapabilities.Play,
                IsAvailable = isAvailable,
            }],
        });
        await using var service = new MediaService(backend);
        await service.StartAsync();
        var result = service.TrySubmit(new(MediaCommandTarget.ForSession(service.Sessions[0].Id), MediaOperation.ActivateSource));
        Assert.AreEqual(expectedStatus, result.Status);
        Assert.IsNull(result.Completion);
        Assert.AreEqual(0, backend.Commands.Length);
    }

    [TestMethod]
    public async Task QueuedCurrentSourceActivationKeepsItsAdmittedTarget()
    {
        var snapshot = Snapshot("First");
        snapshot = snapshot with
        {
            Sessions = [snapshot.Sessions[0], snapshot.Sessions[0] with
            {
                Id = new(2),
                MediaProperties = snapshot.Sessions[0].MediaProperties with { Title = "Second" },
            }],
        };
        var backend = new FakeMediaBackend(snapshot);
        backend.BlockCommands(new(new(1), 1));
        await using var service = new MediaService(new CompositeMediaBackend(Registry(backend)));
        await service.StartAsync();
        await WaitUntilAsync(() => service.Sessions.Length == 2);
        try
        {
            var blocker = Submit(service, MediaCommandTarget.CurrentSession, MediaOperation.SkipNext);
            await backend.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
            var activation = Submit(service, MediaCommandTarget.CurrentSession, MediaOperation.ActivateSource);
            backend.SetSnapshot(snapshot with { Revision = 2, CurrentSessionHints = [new(2)] });
            await WaitUntilAsync(() => service.CurrentSession?.MediaProperties.Title == "Second");
            backend.ReleaseCommands();
            await blocker.Completion!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(MediaCommandOutcomeStatus.Completed,
                (await activation.Completion!.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.AreEqual(new MediaBackendSessionId(1), backend.Commands.Last().SessionId);
            Assert.AreEqual("Second", service.CurrentSession!.MediaProperties.Title);
        }
        finally
        {
            backend.ReleaseCommands();
        }
    }

    [TestMethod]
    [DataRow("rebind")]
    [DataRow("remove")]
    [DataRow("unavailable")]
    [DataRow("disable")]
    public async Task QueuedActivationCannotReachAnObsoleteTarget(string change)
    {
        var original = Snapshot("Original");
        var backend = new FakeMediaBackend(original) { IgnoreCommandCancellation = true };
        backend.BlockCommands();
        await using var composite = new CompositeMediaBackend(Registry(backend));
        await using var service = new MediaService(composite);
        await service.StartAsync();
        await WaitUntilAsync(() => service.Sessions.Length == 1);
        var target = service.Sessions[0].Id;
        Task? disabled = null;
        try
        {
            var blocker = Submit(service, MediaCommandTarget.ForSession(target), MediaOperation.SkipNext);
            await backend.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
            var activation = Submit(service, MediaCommandTarget.ForSession(target), MediaOperation.ActivateSource);
            switch (change)
            {
                case "rebind":
                    backend.SetSnapshot(Snapshot("Replacement", revision: 2, generation: 2));
                    await WaitUntilAsync(() => service.Sessions[0].MediaProperties.Title == "Replacement");
                    break;
                case "unavailable":
                    backend.SetSnapshot(original with { Revision = 2, Sessions = [original.Sessions[0] with { IsAvailable = false }] });
                    await WaitUntilAsync(() => !service.Sessions[0].IsAvailable);
                    break;
                case "remove":
                    backend.SetSnapshot(original with { Revision = 2, Sessions = [], CurrentSessionHints = [] });
                    await WaitUntilAsync(() => service.Sessions.IsEmpty);
                    break;
                case "disable":
                    disabled = composite.SetEnabledAsync("provider0", false);
                    await WaitUntilAsync(() => service.Sessions.IsEmpty);
                    break;
            }

            backend.ReleaseCommands();
            await blocker.Completion!.WaitAsync(TimeSpan.FromSeconds(5));
            var outcome = await activation.Completion!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(outcome.Status is MediaCommandOutcomeStatus.SessionGone or MediaCommandOutcomeStatus.Unavailable);
            Assert.AreEqual(1, backend.Commands.Length);
        }
        finally
        {
            backend.ReleaseCommands();
            if (disabled is not null)
            {
                await disabled.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [TestMethod]
    public async Task SourceExclusionFencesActivationWaitingInsideTheProvider()
    {
        var snapshot = Snapshot("Native browser");
        snapshot = snapshot with
        {
            Sessions = [snapshot.Sessions[0] with
            {
                MediaProperties = snapshot.Sessions[0].MediaProperties with
                {
                    Source = new("Browser") { NativeApplication = new("browser.app") },
                },
            }],
        };
        var native = new FakeSourcePolicyBackend(snapshot) { BlockCommands = true };
        var companion = new FakeMediaBackend(Snapshot("Companion"));
        var registry = new MediaBackendRegistry()
            .Register(new("native", "Native", "Test", _ => native, EnabledByDefault: true))
            .Register(new("companion", "Companion", "Test", _ => companion)
            {
                ReplacesSources = [new("native", "browser.app")],
            });
        await using var composite = new CompositeMediaBackend(registry);
        await using var service = new MediaService(composite);
        await service.StartAsync();
        await WaitUntilAsync(() => service.Sessions.Length == 1);
        var oldId = service.Sessions[0].Id;
        try
        {
            var activation = Submit(service, MediaCommandTarget.ForSession(oldId), MediaOperation.ActivateSource);
            await native.CommandQueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await composite.SetEnabledAsync("companion", true);
            await WaitUntilAsync(() => service.Sessions.All(session => session.Id != oldId));
            native.ReleaseCommands();
            Assert.AreEqual(MediaCommandOutcomeStatus.SessionGone,
                (await activation.Completion!.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.AreEqual(0, native.Inner.Commands.Length);
            Assert.AreEqual(0, companion.Commands.Length);

            await composite.SetEnabledAsync("companion", false);
            await WaitUntilAsync(() => service.Sessions.Any(static session => session.MediaProperties.Title == "Native browser"));
            var restored = service.Sessions.Single();
            Assert.AreNotEqual(oldId, restored.Id);
            Assert.AreEqual(MediaCommandSubmissionStatus.SessionGone,
                service.TrySubmit(new(MediaCommandTarget.ForSession(oldId), MediaOperation.ActivateSource)).Status);
            var fresh = Submit(service, MediaCommandTarget.ForSession(restored.Id), MediaOperation.ActivateSource);
            Assert.AreEqual(MediaCommandOutcomeStatus.Completed,
                (await fresh.Completion!.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        }
        finally
        {
            native.ReleaseCommands();
        }
    }

    [TestMethod]
    public async Task SlowActivationDoesNotBlockPlaybackInAnotherProvider()
    {
        var browser = new FakeMediaBackend(Snapshot("Browser"));
        var player = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Player"));
        browser.BlockCommands();
        await using var service = new MediaService(new CompositeMediaBackend(Registry(browser, player)));
        await service.StartAsync();
        await WaitUntilAsync(() => service.Sessions.Length == 2);
        try
        {
            var browserId = service.Sessions.Single(static session => session.MediaProperties.Title == "Browser").Id;
            var playerId = service.Sessions.Single(static session => session.MediaProperties.Title == "Player").Id;
            var activation = Submit(service, MediaCommandTarget.ForSession(browserId), MediaOperation.ActivateSource);
            await browser.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
            var play = Submit(service, MediaCommandTarget.ForSession(playerId), MediaOperation.Play);
            Assert.AreEqual(MediaCommandOutcomeStatus.Completed,
                (await play.Completion!.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.IsFalse(activation.Completion!.IsCompleted);
        }
        finally
        {
            browser.ReleaseCommands();
        }
    }

    [TestMethod]
    public async Task ActivationDoesNotRequestPlaybackObservations()
    {
        var backend = new FakeMediaBackend(Snapshot("Original")) { SignalOnStart = false };
        await using var service = new MediaService(backend);
        await service.StartAsync();
        Assert.AreEqual(1, backend.SnapshotReadCount);
        backend.SetSnapshotWithoutSignal(Snapshot("Unsignaled change", revision: 2));

        var activation = Submit(service, MediaCommandTarget.CurrentSession, MediaOperation.ActivateSource);
        Assert.AreEqual(MediaCommandOutcomeStatus.Completed,
            (await activation.Completion!.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        await Task.Delay(750);

        Assert.AreEqual(1, backend.SnapshotReadCount);
        Assert.IsTrue(backend.ObservationInvalidations.IsEmpty);
        Assert.AreEqual("Original", service.CurrentSession!.MediaProperties.Title);
        Assert.IsFalse(service.CurrentSession.PlaybackInfo.IsOptimistic);
    }

    private static MediaBackendSnapshot Snapshot(string title, long revision = 1, long generation = 1)
    {
        var snapshot = FakeMediaBackend.CreateSnapshot(revision, title, generation);
        return snapshot with
        {
            Sessions = [snapshot.Sessions[0] with
            {
                Capabilities = MediaCapabilities.ActivateSource | MediaCapabilities.SkipNext,
                MediaProperties = snapshot.Sessions[0].MediaProperties with
                {
                    Source = new("Source"),
                },
            }],
        };
    }

    private static MediaCommandSubmission Submit(MediaService service, MediaCommandTarget target, MediaOperation operation)
    {
        var submission = service.TrySubmit(new(target, operation));
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, submission.Status);
        return submission;
    }

    private static MediaBackendRegistry Registry(params FakeMediaBackend[] backends)
    {
        var registry = new MediaBackendRegistry();
        for (var index = 0; index < backends.Length; index++)
        {
            var backend = backends[index];
            registry.Register(new($"provider{index}", $"Provider {index}", "Test", _ => backend, EnabledByDefault: true));
        }

        return registry;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}