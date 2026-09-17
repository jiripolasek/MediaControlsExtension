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
public sealed class MediaSessionSelectionTests
{
    [TestMethod]
    [DataRow(MediaOperation.SwitchNextSession)]
    [DataRow(MediaOperation.SwitchPreviousSession)]
    public async Task CyclingIncludesPlayersWithOnlyTogglePlayback(MediaOperation operation)
    {
        var snapshot = FakeMediaBackend.CreateSnapshot(1, 1, (1, "Current"), (2, "Toggle player"));
        snapshot = snapshot with
        {
            Sessions = [snapshot.Sessions[0], snapshot.Sessions[1] with { Capabilities = MediaCapabilities.TogglePlayback }],
        };
        var backend = new FakeMediaBackend(snapshot);
        await using var service = new MediaService(backend);
        await service.StartAsync();

        var submission = Submit(service, MediaCommandTarget.ForSession(new(1)), operation);

        Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await submission.Completion!.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.AreEqual(new MediaSessionId(2), service.CurrentSession?.Id);
        Assert.AreEqual(new MediaBackendSessionId(2), backend.Commands.Single().SessionId);
        Assert.AreEqual(MediaOperation.Play, backend.Commands.Single().Operation);
    }

    [TestMethod]
    public async Task SelectedProviderSurvivesUnrelatedRefreshesAndKeepsCommandRouting()
    {
        var first = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "First", playbackState: MediaPlaybackState.Playing));
        var second = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Second"));
        second.BlockCommands();
        await using var service = new MediaService(new CompositeMediaBackend(Registry(first, second)));
        await service.StartAsync();
        await WaitUntilAsync(() => service.Sessions.Length == 2);
        var selected = service.Sessions.Single(static session => session.MediaProperties.Title == "Second");
        var selectedNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CurrentSessionChanged += (_, _) =>
        {
            if (service.CurrentSession == selected)
            {
                selectedNotification.TrySetResult();
            }
        };

        try
        {
            var switchSession = Submit(service, MediaCommandTarget.CurrentSession, MediaOperation.SwitchNextSession);
            Assert.AreSame(selected, service.CurrentSession);
            await second.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
            await selectedNotification.Task.WaitAsync(TimeSpan.FromSeconds(5));

            first.SetSnapshot(FakeMediaBackend.CreateSnapshot(2, "First updated", playbackState: MediaPlaybackState.Playing));
            second.SetSnapshot(FakeMediaBackend.CreateSnapshot(2, "Second updated"));
            await WaitUntilAsync(() => service.Sessions.All(static session => session.MediaProperties.Title.EndsWith("updated", StringComparison.Ordinal)));
            Assert.AreSame(selected, service.CurrentSession);

            var skip = Submit(service, MediaCommandTarget.CurrentSession, MediaOperation.SkipNext);
            second.ReleaseCommands();
            await Task.WhenAll(switchSession.Completion!, skip.Completion!).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(selected.Id, (await skip.Completion!).SessionId);
            CollectionAssert.AreEqual(new[] { MediaOperation.Play, MediaOperation.SkipNext },
                second.Commands.Select(static command => command.Operation).ToArray());
            Assert.IsEmpty(first.Commands);
        }
        finally
        {
            second.ReleaseCommands();
        }
    }

    [TestMethod]
    [DataRow(MediaOperation.SwitchNextSession)]
    [DataRow(MediaOperation.SwitchPreviousSession)]
    public async Task CyclingSkipsUnavailableAndUnplayableSessions(MediaOperation operation)
    {
        var snapshot = FakeMediaBackend.CreateSnapshot(1, 1,
            (1, "Current"), (2, "Disconnected"), (3, "Cannot play"), (4, "Target"));
        snapshot = snapshot with
        {
            Sessions = [snapshot.Sessions[0], snapshot.Sessions[1] with { IsAvailable = false },
                snapshot.Sessions[2] with { Capabilities = MediaCapabilities.Pause }, snapshot.Sessions[3]],
        };
        if (operation == MediaOperation.SwitchPreviousSession)
        {
            snapshot = snapshot with { Sessions = [snapshot.Sessions[0], snapshot.Sessions[3], snapshot.Sessions[2], snapshot.Sessions[1]] };
        }

        var backend = new FakeMediaBackend(snapshot);
        await using var service = new MediaService(backend);
        await service.StartAsync();
        var submission = Submit(service, MediaCommandTarget.CurrentSession, operation);
        Assert.AreEqual(new MediaSessionId(4), service.CurrentSession?.Id);
        Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await submission.Completion!.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.AreEqual(new MediaBackendSessionId(4), backend.Commands.Single().SessionId);
        var wrap = Submit(service, MediaCommandTarget.CurrentSession, operation);
        Assert.AreEqual(new MediaSessionId(1), service.CurrentSession?.Id);
        Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await wrap.Completion!.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.AreEqual(new MediaBackendSessionId(1), backend.Commands[1].SessionId);
    }

    [TestMethod]
    public async Task AutomaticSelectionPrefersAPlayingProviderHint()
    {
        var first = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, 1,
            (1, "Paused hint", MediaPlaybackState.Paused), (2, "Playing without hint", MediaPlaybackState.Playing)));
        var second = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, 2,
            (1, "Another playing session", MediaPlaybackState.Playing), (2, "Playing hint", MediaPlaybackState.Playing)));
        await using var service = new MediaService(new CompositeMediaBackend(Registry(first, second)));
        await service.StartAsync();
        await WaitUntilAsync(() => service.Sessions.Length == 4);
        Assert.AreEqual("Playing hint", service.CurrentSession?.MediaProperties.Title);
    }

    [TestMethod]
    [DataRow(MediaBackendCommandStatus.Completed)]
    [DataRow(MediaBackendCommandStatus.Failed)]
    public async Task LatePlayCompletionDoesNotReplaceANewerSelection(MediaBackendCommandStatus result)
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, 1, (1, "First"), (2, "Second"), (3, "Third")));
        backend.BlockCommands(new(new(2), 1));
        await using var service = new MediaService(backend);
        await service.StartAsync();
        try
        {
            var earlier = Submit(service, MediaCommandTarget.ForSession(new(2)), MediaOperation.Play);
            await backend.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(new MediaSessionId(2), service.CurrentSession?.Id);
            var newer = Submit(service, MediaCommandTarget.ForSession(new(3)), MediaOperation.TogglePlayback);
            Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await newer.Completion!.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.AreEqual(new MediaSessionId(3), service.CurrentSession?.Id);

            backend.CommandResult = new(result, null);
            backend.ReleaseCommands();
            var outcome = await earlier.Completion!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(result == MediaBackendCommandStatus.Completed ? MediaCommandOutcomeStatus.Completed : MediaCommandOutcomeStatus.Failed,
                outcome.Status);
            Assert.AreEqual(new MediaSessionId(2), outcome.SessionId);
            Assert.AreEqual(new MediaSessionId(3), service.CurrentSession?.Id);

            backend.SetSnapshot(FakeMediaBackend.CreateSnapshot(2, 1, (1, "First"), (2, "Second"), (3, "Third updated")));
            await WaitUntilAsync(() => service.Sessions[2].MediaProperties.Title == "Third updated");
            Assert.AreEqual(new MediaSessionId(3), service.CurrentSession?.Id);
        }
        finally
        {
            backend.ReleaseCommands();
        }
    }

    [TestMethod]
    public async Task DisablingTheSelectedProviderFallsBackWithoutRestoringItsOldChoice()
    {
        var first = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "First"));
        var instances = new List<FakeMediaBackend>();
        var registry = Registry(first).Register(new("selected", "Selected provider", "Test provider", _ =>
        {
            var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Second"));
            instances.Add(backend);
            return backend;
        }, EnabledByDefault: true));
        var composite = new CompositeMediaBackend(registry);
        await using var service = new MediaService(composite);
        await service.StartAsync();
        await WaitUntilAsync(() => service.Sessions.Length == 2);
        var selected = service.Sessions.Single(static session => session.MediaProperties.Title == "Second");
        await Submit(service, MediaCommandTarget.ForSession(selected.Id), MediaOperation.Play).Completion!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreSame(selected, service.CurrentSession);

        await composite.SetEnabledAsync("selected", false);
        await WaitUntilAsync(() => service.Sessions.Length == 1);
        Assert.AreEqual("First", service.CurrentSession?.MediaProperties.Title);
        Assert.IsFalse(selected.IsAvailable);

        await composite.SetEnabledAsync("selected", true);
        await WaitUntilAsync(() => service.Sessions.Length == 2);
        Assert.AreEqual("First", service.CurrentSession?.MediaProperties.Title);
        var replacement = service.Sessions.Single(static session => session.MediaProperties.Title == "Second");
        Assert.AreNotEqual(selected.Id, replacement.Id);
        Assert.AreEqual(MediaCommandSubmissionStatus.SessionGone,
            service.TrySubmit(new(MediaCommandTarget.ForSession(selected.Id), MediaOperation.Play)).Status);

        instances[1].SetSnapshot(FakeMediaBackend.CreateSnapshot(2, "Second playing", playbackState: MediaPlaybackState.Playing));
        await WaitUntilAsync(() => service.CurrentSession?.MediaProperties.Title == "Second playing");
        Assert.AreSame(replacement, service.CurrentSession);
    }

    [TestMethod]
    [DataRow(MediaOperation.SwitchNextSession)]
    [DataRow(MediaOperation.SwitchPreviousSession)]
    public async Task CyclingWithoutAnotherPlayableSessionIsRejected(MediaOperation operation)
    {
        var snapshot = FakeMediaBackend.CreateSnapshot(1, 1, (1, "Current"), (2, "Disconnected"), (3, "Cannot play"));
        var backend = new FakeMediaBackend(snapshot with
        {
            Sessions = [snapshot.Sessions[0], snapshot.Sessions[1] with { IsAvailable = false },
                snapshot.Sessions[2] with { Capabilities = MediaCapabilities.Pause }],
        });
        await using var service = new MediaService(backend);
        await service.StartAsync();
        Assert.AreEqual(MediaCommandSubmissionStatus.Unsupported, service.TrySubmit(new(MediaCommandTarget.CurrentSession, operation)).Status);
        Assert.AreEqual(new MediaSessionId(1), service.CurrentSession?.Id);
        Assert.IsEmpty(backend.Commands);
    }

    [TestMethod]
    public async Task RejectedPlayAndTargetedStopDoNotSelectTheTarget()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, 1, (1, "Current"), (2, "Other")));
        backend.BlockCommands();
        await using var service = new MediaService(backend);
        await service.StartAsync();
        try
        {
            var target = MediaCommandTarget.ForSession(new(2));
            Submit(service, target, MediaOperation.SkipNext);
            await backend.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
            Submit(service, target, MediaOperation.Stop);
            Assert.AreEqual(MediaCommandSubmissionStatus.Busy, service.TrySubmit(new(target, MediaOperation.Play)).Status);
            Assert.AreEqual(new MediaSessionId(1), service.CurrentSession?.Id);
        }
        finally
        {
            backend.ReleaseCommands();
        }
    }

    [TestMethod]
    public void AutomaticSelectionKeepsEqualCandidatesStableAndFollowsBetterCandidates()
    {
        var store = new MediaStateStore();
        var snapshot = FakeMediaBackend.CreateSnapshot(1, 1, (1, "First"), (2, "Second")) with
        {
            CurrentSessionHints = [new(1), new(2)],
        };
        Assert.AreEqual(new MediaSessionId(1), store.ApplyBackendSnapshot(snapshot).CurrentSessionId);
        snapshot = snapshot with { Revision = 2, Sessions = [snapshot.Sessions[1], snapshot.Sessions[0]], CurrentSessionHints = [new(2), new(1)] };
        Assert.AreEqual(new MediaSessionId(1), store.ApplyBackendSnapshot(snapshot).CurrentSessionId);

        snapshot = snapshot with { Revision = 3, CurrentSessionHints = [new(2)] };
        Assert.AreEqual(new MediaSessionId(2), store.ApplyBackendSnapshot(snapshot).CurrentSessionId);
        snapshot = snapshot with
        {
            Revision = 4,
            Sessions = [snapshot.Sessions[0], snapshot.Sessions[1] with { PlaybackState = MediaPlaybackState.Playing }],
        };
        Assert.AreEqual(new MediaSessionId(1), store.ApplyBackendSnapshot(snapshot).CurrentSessionId);
        snapshot = snapshot with
        {
            Revision = 5,
            Sessions = [snapshot.Sessions[0] with { PlaybackState = MediaPlaybackState.Playing }, snapshot.Sessions[1]],
        };
        Assert.AreEqual(new MediaSessionId(2), store.ApplyBackendSnapshot(snapshot).CurrentSessionId);
    }

    [TestMethod]
    public void MissingOrUnavailableHintsFallBackToAvailableSessions()
    {
        var store = new MediaStateStore();
        var snapshot = FakeMediaBackend.CreateSnapshot(1, 1, (1, "Unavailable hint"), (2, "Available"), (3, "Another available"));
        snapshot = snapshot with { Sessions = [snapshot.Sessions[0] with { IsAvailable = false }, snapshot.Sessions[1], snapshot.Sessions[2]] };
        Assert.AreEqual(new MediaSessionId(2), store.ApplyBackendSnapshot(snapshot).CurrentSessionId);
        snapshot = snapshot with
        {
            Revision = 2,
            Sessions = [snapshot.Sessions[2], snapshot.Sessions[1], snapshot.Sessions[0]],
            CurrentSessionHints = [new(99)],
        };
        Assert.AreEqual(new MediaSessionId(2), store.ApplyBackendSnapshot(snapshot).CurrentSessionId);
        Assert.IsNull(store.ApplyBackendSnapshot(snapshot with
        {
            Revision = 3,
            Sessions = [.. snapshot.Sessions.Select(static session => session with { IsAvailable = false })],
        }).CurrentSessionId);
    }

    [TestMethod]
    public void SelectionSurvivesRebindingUntilItBecomesUnavailable()
    {
        var store = new MediaStateStore();
        var snapshot = FakeMediaBackend.CreateSnapshot(1, 1, (1, "First"), (2, "Selected"));
        store.ApplyBackendSnapshot(snapshot);
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted,
            store.TryResolveCommand(new(MediaCommandTarget.ForSession(new(2)), MediaOperation.Play), out var command));
        Assert.AreEqual(new MediaSessionId(2), store.ApplyAcceptedCommand(command, new(1)).CurrentSessionId);
        snapshot = snapshot with { Revision = 2, Sessions = [snapshot.Sessions[0], snapshot.Sessions[1] with { BindingGeneration = 2 }] };
        Assert.AreEqual(new MediaSessionId(2), store.ApplyBackendSnapshot(snapshot).CurrentSessionId);
        Assert.AreEqual(new MediaSessionId(1), store.ApplyBackendSnapshot(snapshot with
        {
            Revision = 3,
            Sessions = [snapshot.Sessions[0], snapshot.Sessions[1] with { IsAvailable = false }],
        }).CurrentSessionId);
        var reconnected = snapshot with { Revision = 4, Sessions = [snapshot.Sessions[0], snapshot.Sessions[1] with { BindingGeneration = 3 }] };
        Assert.AreEqual(new MediaSessionId(1), store.ApplyBackendSnapshot(reconnected).CurrentSessionId);
        Assert.AreEqual(new MediaSessionId(2), store.ApplyBackendSnapshot(reconnected with { Revision = 5, CurrentSessionHints = [new(2)] }).CurrentSessionId);
    }

    [TestMethod]
    public void FailedPlayRollsBackPlaybackButKeepsTheSelectedSession()
    {
        var store = new MediaStateStore();
        var snapshot = FakeMediaBackend.CreateSnapshot(1, 1, (1, "First"), (2, "Selected"));
        store.ApplyBackendSnapshot(snapshot);
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted,
            store.TryResolveCommand(new(MediaCommandTarget.ForSession(new(2)), MediaOperation.Play), out var command));
        store.ApplyAcceptedCommand(command, new(1));
        var result = store.CompleteCommand(command, new(1), succeeded: false);
        Assert.AreEqual(new MediaSessionId(2), result.CurrentSessionId);
        Assert.AreEqual(MediaPlaybackState.Paused, result.Sessions[1].PlaybackInfo.EffectiveState);
        Assert.IsFalse(result.Sessions[1].PlaybackInfo.IsOptimistic);
        Assert.AreEqual(new MediaSessionId(2), store.ApplyBackendSnapshot(snapshot with { Revision = 2 }).CurrentSessionId);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void BindingChangesDuringAdmissionDoNotSelectOrPredictTheObsoleteTarget(bool rebound)
    {
        var store = new MediaStateStore();
        var snapshot = FakeMediaBackend.CreateSnapshot(1, 1, (1, "Current"), (2, "Target"));
        store.ApplyBackendSnapshot(snapshot);
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted,
            store.TryResolveCommand(new(MediaCommandTarget.ForSession(new(2)), MediaOperation.Play), out var command));
        store.ApplyBackendSnapshot(snapshot with
        {
            Revision = 2,
            Sessions = [snapshot.Sessions[0], snapshot.Sessions[1] with { BindingGeneration = rebound ? 2 : 1, IsAvailable = rebound }],
        });
        var result = store.ApplyAcceptedCommand(command, new(1));
        Assert.AreEqual(new MediaSessionId(1), result.CurrentSessionId);
        Assert.IsFalse(result.Sessions[1].PlaybackInfo.IsOptimistic);
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
            registry.Register(new($"provider{index}", $"Provider {index}", "Test provider", _ => backend, EnabledByDefault: true));
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