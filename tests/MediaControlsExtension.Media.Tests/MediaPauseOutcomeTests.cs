// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class MediaPauseOutcomeTests
{
    [TestMethod]
    [DataRow(MediaBackendCommandStatus.Completed, MediaCommandOutcomeStatus.Completed)]
    [DataRow(MediaBackendCommandStatus.Failed, MediaCommandOutcomeStatus.Failed)]
    public async Task MixedPausesKeepThePrimaryResultAndCapturedPublicIdentities(
        MediaBackendCommandStatus primaryStatus, MediaCommandOutcomeStatus expectedStatus)
    {
        var target = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Target", bindingGeneration: 5))
        {
            CommandResult = new(primaryStatus, "Primary diagnostic"),
        };
        var failed = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Failed pause", bindingGeneration: 7))
        {
            CommandResult = new(MediaBackendCommandStatus.Failed, "Pause diagnostic"),
        };
        var healthy = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Healthy", bindingGeneration: 9));
        await using var composite = new CompositeMediaBackend(Registry(target, failed, healthy));
        await using var service = new MediaService(composite);
        service.UpdateOptions(new(PauseOtherSessionsOnPlay: true));
        await service.StartAsync();
        await WaitUntilAsync(() => Task.FromResult(service.Sessions.Length == 3));
        var targetSession = service.Sessions.Single(static session => session.MediaProperties.Title == "Target");
        var failedSession = service.Sessions.Single(static session => session.MediaProperties.Title == "Failed pause");
        var healthySession = service.Sessions.Single(static session => session.MediaProperties.Title == "Healthy");
        var submission = service.TrySubmit(new(MediaCommandTarget.ForSession(targetSession.Id), MediaOperation.Play));
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, submission.Status);
        var outcome = await submission.Completion!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(expectedStatus, outcome.Status);
        Assert.AreEqual("Primary diagnostic", outcome.DiagnosticMessage);
        Assert.AreEqual(targetSession.Id, outcome.SessionId);
        Assert.AreEqual(submission.OperationId, outcome.OperationId);
        Assert.AreEqual(targetSession.Id, service.CurrentSession?.Id);
        Assert.AreEqual(2, outcome.PauseOutcomes.Length);
        Assert.AreEqual(new MediaPauseOutcome(failedSession.Id, 7, MediaCommandOutcomeStatus.Failed, "Pause diagnostic"),
            outcome.PauseOutcomes.Single(pause => pause.SessionId == failedSession.Id));
        Assert.AreEqual(new MediaPauseOutcome(healthySession.Id, 9, MediaCommandOutcomeStatus.Completed, null),
            outcome.PauseOutcomes.Single(pause => pause.SessionId == healthySession.Id));
        Assert.AreEqual(1L, target.Commands.Single().SessionId.Value);
        Assert.AreEqual(1L, failed.Commands.Single().SessionId.Value);
        Assert.AreEqual(1L, healthy.Commands.Single().SessionId.Value);
        Assert.AreEqual(MediaOperation.Play, target.Commands.Single().Operation);
    }

    [TestMethod]
    public async Task MissingAndUnsupportedPausesAreReportedWithoutDispatchOrDuplicates()
    {
        var target = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Target"));
        var unsupportedSnapshot = FakeMediaBackend.CreateSnapshot(1, "Unsupported", bindingGeneration: 7);
        var unsupported = new FakeMediaBackend(unsupportedSnapshot with
        {
            Sessions = [unsupportedSnapshot.Sessions.Single() with { Capabilities = MediaCapabilities.Play }],
        });
        await using var composite = new CompositeMediaBackend(Registry(target, unsupported));
        await composite.StartAsync(default);
        var snapshot = await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 2);
        var targetSession = snapshot.Sessions.Single(static session => session.MediaProperties.Title == "Target");
        var unsupportedSession = snapshot.Sessions.Single(static session => session.MediaProperties.Title == "Unsupported");
        var unsupportedTarget = new MediaBackendSessionTarget(unsupportedSession.Id, unsupportedSession.BindingGeneration);
        var missingTarget = new MediaBackendSessionTarget(new(long.MaxValue), 17);
        var result = await composite.ExecuteAsync(new(targetSession.Id, targetSession.BindingGeneration, MediaOperation.Play,
            [unsupportedTarget, missingTarget, unsupportedTarget, new(targetSession.Id, targetSession.BindingGeneration)]), default);

        Assert.AreEqual(MediaBackendCommandStatus.Completed, result.Status);
        Assert.IsNull(result.DiagnosticMessage);
        Assert.AreEqual(2, result.PauseResults.Length);
        Assert.AreEqual(MediaBackendCommandStatus.Unsupported, result.PauseResults.Single(pause => pause.Target == unsupportedTarget).Status);
        Assert.AreEqual(MediaBackendCommandStatus.SessionGone, result.PauseResults.Single(pause => pause.Target == missingTarget).Status);
        Assert.IsEmpty(unsupported.Commands);
        Assert.AreEqual(MediaOperation.Play, target.Commands.Single().Operation);
    }

    [TestMethod]
    public async Task TimedOutPauseRemainsUnavailableAfterTheUnderlyingCallCompletes()
    {
        var target = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Target"));
        var slow = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Slow"))
        {
            IgnoreCommandCancellation = true,
        };
        slow.BlockCommands();
        await using var composite = new CompositeMediaBackend(Registry(target, slow), operationTimeout: TimeSpan.FromMilliseconds(100));
        await composite.StartAsync(default);
        var snapshot = await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 2);
        var targetSession = snapshot.Sessions.Single(static session => session.MediaProperties.Title == "Target");
        var slowSession = snapshot.Sessions.Single(static session => session.MediaProperties.Title == "Slow");
        try
        {
            var result = await composite.ExecuteAsync(new(targetSession.Id, targetSession.BindingGeneration, MediaOperation.Play,
                [new(slowSession.Id, slowSession.BindingGeneration)]), default).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(MediaBackendCommandStatus.Completed, result.Status);
            var pause = result.PauseResults.Single();
            Assert.AreEqual(MediaBackendCommandStatus.Unavailable, pause.Status);
            Assert.IsFalse(string.IsNullOrWhiteSpace(pause.DiagnosticMessage));
            Assert.IsFalse(slow.CommandEvents.Last().Completed);
            Assert.AreEqual(MediaOperation.Play, target.Commands.Single().Operation);

            slow.ReleaseCommands();
            await WaitUntilAsync(() => Task.FromResult(slow.CommandEvents.Last().Completed));
            Assert.AreSame(pause, result.PauseResults.Single());
            Assert.AreEqual(MediaBackendCommandStatus.Unavailable, pause.Status);
        }
        finally
        {
            slow.ReleaseCommands();
        }
    }

    [TestMethod]
    public async Task DisablingASecondaryProviderReportsTheActivePauseAndSkipsItsQueuedBinding()
    {
        var target = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Target"));
        var others = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, 1, (1, "First"), (2, "Second")));
        others.BlockCommands(new(new(1), 1));
        await using var composite = new CompositeMediaBackend(Registry(target, others));
        await composite.StartAsync(default);
        var snapshot = await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 3);
        var targetSession = snapshot.Sessions.Single(static session => session.MediaProperties.Title == "Target");
        var first = snapshot.Sessions.Single(static session => session.MediaProperties.Title == "First");
        var second = snapshot.Sessions.Single(static session => session.MediaProperties.Title == "Second");
        try
        {
            var play = composite.ExecuteAsync(new(targetSession.Id, targetSession.BindingGeneration, MediaOperation.Play,
                [new(first.Id, first.BindingGeneration), new(second.Id, second.BindingGeneration)]), default);
            await others.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
            await composite.SetEnabledAsync("provider1", false);
            var result = await play.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(MediaBackendCommandStatus.Completed, result.Status);
            Assert.AreEqual(2, result.PauseResults.Length);
            Assert.AreEqual(MediaBackendCommandStatus.Unavailable, result.PauseResults.Single(pause => pause.Target.SessionId == first.Id).Status);
            Assert.AreEqual(MediaBackendCommandStatus.SessionGone, result.PauseResults.Single(pause => pause.Target.SessionId == second.Id).Status);
            Assert.AreEqual(1, others.Commands.Length);
            Assert.AreEqual(1, others.DisposeCount);
        }
        finally
        {
            others.ReleaseCommands();
        }
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

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}