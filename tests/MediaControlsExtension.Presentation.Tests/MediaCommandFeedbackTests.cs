// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using JPSoftworks.MediaControlsExtension.Commands;
using JPSoftworks.MediaControlsExtension.Helpers;
using JPSoftworks.MediaControlsExtension.Media;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;
using static JPSoftworks.MediaControlsExtension.Presentation.Tests.PlaybackPresentationTestSupport;

namespace JPSoftworks.MediaControlsExtension.Presentation.Tests;

[TestClass]
public sealed class MediaCommandFeedbackTests
{
    [TestMethod]
    [DataRow(MediaCommandOutcomeStatus.Unavailable)]
    [DataRow(MediaCommandOutcomeStatus.Unsupported)]
    [DataRow(MediaCommandOutcomeStatus.Failed)]
    [DataRow(MediaCommandOutcomeStatus.SessionGone)]
    public async Task LatePrimaryFailureWarnsAfterOptimisticFeedback(MediaCommandOutcomeStatus status)
    {
        var completion
            = new TaskCompletionSource<MediaCommandOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new List<string>();
        using var notifier = new MediaCommandFailureNotifier(static () => true, messages.Add);
        var observation = notifier.ObserveAsync(completion.Task);
        Assert.IsEmpty(messages);

        completion.SetResult(new(new(1), status, new(1), "No command was sent"));
        await observation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(
            status == MediaCommandOutcomeStatus.SessionGone
                ? $"\U0001F622 {Strings.Toast_NoCurrentSession}"
                : $"{(status == MediaCommandOutcomeStatus.Unsupported ? "\U0001F6AB" : "\U0001F622")} {Strings.Toast_NothingHappened}",
            messages.Single());
        using var service = new OutcomeService(await completion.Task);
        Assert.AreEqual(messages.Single(), await new PlayPauseMop().InvokeAsync(service, MediaCommandTarget.CurrentSession, default));
    }

    [TestMethod]
    public async Task UnconfirmedPredecessorWarnsWhenDependentPlayIsAbandoned()
    {
        var first = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "First"))
        {
            CommandResult = new(MediaBackendCommandStatus.Unconfirmed, "No confirmation"),
        };
        var second = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Second"));
        first.BlockCommands();
        var registry = new MediaBackendRegistry()
            .Register(new("first", "First", "Test provider", _ => first, EnabledByDefault: true))
            .Register(new("second", "Second", "Test provider", _ => second, EnabledByDefault: true));
        await using var service = new MediaService(new CompositeMediaBackend(registry));
        service.UpdateOptions(new(PauseOtherSessionsOnPlay: true));
        await service.StartAsync();
        await WaitUntilAsync(() => service.Sessions.Length == 2);
        try
        {
            var firstTarget = MediaCommandTarget.ForSession(service.Sessions.Single(static session =>
                session.MediaProperties.Title == "First").Id);
            var secondTarget = MediaCommandTarget.ForSession(service.Sessions.Single(static session =>
                session.MediaProperties.Title == "Second").Id);
            var playFirst = service.TrySubmit(new(firstTarget, MediaOperation.Play));
            await first.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
            var pauseFirst = service.TrySubmit(new(firstTarget, MediaOperation.Pause));
            var playSecond = service.TrySubmit(new(secondTarget, MediaOperation.Play));
            Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, pauseFirst.Status);
            Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, playSecond.Status);
            var messages = new List<string>();
            using var notifier = new MediaCommandFailureNotifier(static () => true, messages.Add);
            var observation = notifier.ObserveAsync(playSecond.Completion!);

            first.ReleaseCommands();
            var outcomes = await Task.WhenAll(playFirst.Completion!, pauseFirst.Completion!, playSecond.Completion!)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await observation.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(MediaCommandOutcomeStatus.Unconfirmed, outcomes[0].Status);
            Assert.AreEqual(MediaCommandOutcomeStatus.Abandoned, outcomes[1].Status);
            Assert.AreEqual(MediaCommandOutcomeStatus.Abandoned, outcomes[2].Status);
            Assert.IsEmpty(second.Commands);
            Assert.HasCount(1, messages);
            Assert.AreEqual("The playback request was canceled because an earlier change could not be confirmed.",
                messages.Single());
        }
        finally
        {
            first.ReleaseCommands();
        }
    }

    [TestMethod]
    public async Task ShutdownAndSupersededCommandsDoNotShowFailureWarnings()
    {
        var messages = new List<string>();
        using var notifier = new MediaCommandFailureNotifier(static () => true, messages.Add);
        await notifier.ObserveAsync(Task.FromResult(PartialOutcome() with
        {
            Status = MediaCommandOutcomeStatus.Canceled
        }));
        await notifier.ObserveAsync(Task.FromResult(PartialOutcome() with
        {
            Status = MediaCommandOutcomeStatus.Superseded
        }));
        Assert.IsEmpty(messages);
    }

    [TestMethod]
    [DataRow(MediaCommandOutcomeStatus.Unconfirmed)]
    [DataRow(MediaCommandOutcomeStatus.Abandoned)]
    public async Task RestartRequirementTakesPrecedenceOverPlaybackWarnings(MediaCommandOutcomeStatus status)
    {
        using var service = new OutcomeService(PartialOutcome() with { Status = status })
        {
            Availability = MediaControlAvailability.CircuitOpen,
        };

        var message = await new PlayOperation().InvokeAsync(service, MediaCommandTarget.CurrentSession, default);

        StringAssert.Contains(message, Strings.Toast_MediaControlsUnavailable);
    }

    [TestMethod]
    public async Task UnconfirmedPlaybackWarnsOnceWhileReplacedPlaybackStaysSilent()
    {
        var messages = new List<string>();
        using var notifier = new MediaCommandFailureNotifier(static () => true, messages.Add);
        await notifier.ObserveAsync(Task.FromResult(PartialOutcome() with
        {
            Status = MediaCommandOutcomeStatus.Unconfirmed
        }));
        await notifier.ObserveAsync(Task.FromResult(PartialOutcome() with
        {
            Status = MediaCommandOutcomeStatus.Superseded
        }));
        Assert.AreEqual("Playback could not be confirmed.", messages.Single());
    }

    [TestMethod]
    public async Task WaitingPlaybackReportsUncertaintyAndSuppressesReplacedCommands()
    {
        using var unconfirmed
            = new OutcomeService(PartialOutcome() with { Status = MediaCommandOutcomeStatus.Unconfirmed });
        using var replaced
            = new OutcomeService(PartialOutcome() with { Status = MediaCommandOutcomeStatus.Superseded });
        using var abandoned
            = new OutcomeService(PartialOutcome() with { Status = MediaCommandOutcomeStatus.Abandoned });
        Assert.AreEqual("Playback could not be confirmed.",
            await new PlayOperation().InvokeAsync(unconfirmed, MediaCommandTarget.CurrentSession, default));
        Assert.IsNull(await new PlayOperation().InvokeAsync(replaced, MediaCommandTarget.CurrentSession, default));
        Assert.AreEqual("The playback request was canceled because an earlier change could not be confirmed.",
            await new PlayOperation().InvokeAsync(abandoned, MediaCommandTarget.CurrentSession, default));
    }

    [TestMethod]
    public async Task WaitingPlayIncludesFailuresWithoutReplacingItsSuccessMessage()
    {
        using var service = new OutcomeService(PartialOutcome());
        var message = await new PlayOperation().InvokeAsync(service, MediaCommandTarget.CurrentSession, default);

        Assert.AreEqual("Playing requested player. Could not pause another player.", message);
    }

    [TestMethod]
    public async Task WaitingPlayRetainsItsPrimaryFailureMessage()
    {
        using var service = new OutcomeService(PartialOutcome() with { Status = MediaCommandOutcomeStatus.Failed });
        var message = await new PlayOperation().InvokeAsync(service, MediaCommandTarget.CurrentSession, default);

        Assert.AreEqual("Primary play failed.", message);
    }

    [TestMethod]
    public void UnavailablePausesWarnWhileMissingAndUnsupportedBindingsAreSkipped()
    {
        var outcome = PartialOutcome() with
        {
            PauseOutcomes =
            [
                new(new(2), 7, MediaCommandOutcomeStatus.Failed, "Rejected"),
                new(new(3), 9, MediaCommandOutcomeStatus.Unavailable, "Disconnected"),
                new(new(4), 11, MediaCommandOutcomeStatus.SessionGone, "Replaced"),
                new(new(5), 13, MediaCommandOutcomeStatus.Unsupported, "No pause support"),
                new(new(6), 15, MediaCommandOutcomeStatus.Completed, null),
            ],
        };

        Assert.AreEqual("Playing requested player. Could not pause 2 other players.",
            MediaCommandFeedback.AppendPauseWarning("Playing requested player.", outcome));
    }

    [TestMethod]
    public void SkippedPausesAndCanceledPrimaryCommandsDoNotProduceWarnings()
    {
        var skipped = PartialOutcome() with
        {
            PauseOutcomes =
            [
                new(new(2), 7, MediaCommandOutcomeStatus.SessionGone, "Replaced"),
                new(new(3), 9, MediaCommandOutcomeStatus.Unsupported, "No pause support"),
            ],
        };

        Assert.AreEqual("Playing requested player.",
            MediaCommandFeedback.AppendPauseWarning("Playing requested player.", skipped));
        Assert.IsNull(MediaCommandFeedback.GetPauseWarning(PartialOutcome() with
        {
            Status = MediaCommandOutcomeStatus.Canceled
        }));
        Assert.IsNull(MediaCommandFeedback.GetPauseWarning(skipped with { PauseOutcomes = [] }));
    }

    [TestMethod]
    public async Task ImmediateFeedbackCanReturnBeforeTheLatePauseWarning()
    {
        var completion
            = new TaskCompletionSource<MediaCommandOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new List<string>();
        using var notifier = new MediaCommandFailureNotifier(static () => true, messages.Add);
        var observation = notifier.ObserveAsync(completion.Task);

        Assert.IsFalse(observation.IsCompleted);
        Assert.IsEmpty(messages);
        completion.SetResult(PartialOutcome());
        await observation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual("Could not pause another player.", messages.Single());
    }

    [TestMethod]
    [DataRow(true, false, MediaCommandOutcomeStatus.Completed)]
    [DataRow(false, true, MediaCommandOutcomeStatus.Completed)]
    [DataRow(true, false, MediaCommandOutcomeStatus.Abandoned)]
    [DataRow(false, true, MediaCommandOutcomeStatus.Abandoned)]
    public async Task LateWarningsRespectTheToastSettingAtCompletion(
        bool initialSetting, bool finalSetting, MediaCommandOutcomeStatus status)
    {
        var enabled = initialSetting;
        var completion
            = new TaskCompletionSource<MediaCommandOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new List<string>();
        using var notifier = new MediaCommandFailureNotifier(() => enabled, messages.Add);
        var observation = notifier.ObserveAsync(completion.Task);
        enabled = finalSetting;
        completion.SetResult(PartialOutcome() with { Status = status });
        await observation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(finalSetting ? 1 : 0, messages.Count);
    }

    [TestMethod]
    public async Task DisposalStopsLateWarningsWithoutCancelingTheMediaCommand()
    {
        var completion
            = new TaskCompletionSource<MediaCommandOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new List<string>();
        using var notifier = new MediaCommandFailureNotifier(static () => true, messages.Add);
        var observation = notifier.ObserveAsync(completion.Task);
        notifier.Dispose();
        await observation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsFalse(completion.Task.IsCompleted);
        completion.SetResult(PartialOutcome());
        await notifier.ObserveAsync(completion.Task);
        Assert.IsEmpty(messages);
    }

    [TestMethod]
    public async Task NotificationFailuresDoNotFaultTheMediaCompletionOrLaterNotifications()
    {
        var attempts = 0;
        using var notifier = new MediaCommandFailureNotifier(static () => true, _ =>
        {
            if (++attempts == 1)
            {
                throw new InvalidOperationException("Host disconnected");
            }
        });
        var completion = Task.FromResult(PartialOutcome());
        await notifier.ObserveAsync(completion);
        await notifier.ObserveAsync(completion);

        Assert.AreEqual(2, attempts);
        Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await completion).Status);
    }

    [TestMethod]
    public async Task FaultedCompletionIsObservedWithoutShowingASuccessWarning()
    {
        var messages = new List<string>();
        using var notifier = new MediaCommandFailureNotifier(static () => true, messages.Add);
        await notifier.ObserveAsync(
            Task.FromException<MediaCommandOutcome>(new InvalidOperationException("Completion failed")));

        Assert.IsEmpty(messages);
    }

    private static MediaCommandOutcome PartialOutcome() =>
        new(new(1), MediaCommandOutcomeStatus.Completed, new(1), null)
        {
            PauseOutcomes = [new(new(2), 7, MediaCommandOutcomeStatus.Failed, "Pause rejected")],
        };

    private sealed class PlayOperation : MediaSessionOp
    {
        public override MediaOperation Operation => MediaOperation.Play;

        protected override ValueTask<string> GetSuccessMessageAsync(
            IMediaService mediaService,
            MediaCommandOutcome outcome,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult("Playing requested player.");

        protected override string GetFailureMessage(object status) => "Primary play failed.";
    }

    private sealed class OutcomeService(MediaCommandOutcome outcome) : IMediaService
    {
        public event EventHandler? BackendsChanged { add { } remove { } }
        public event EventHandler? CurrentSessionChanged { add { } remove { } }
        public event EventHandler? SessionsChanged { add { } remove { } }
        public event EventHandler? StatusChanged { add { } remove { } }
        public ImmutableArray<MediaBackendState> Backends => [];
        public ImmutableArray<MediaSession> Sessions => [];
        public MediaSession? CurrentSession => null;
        public MediaServiceStatus Status => MediaServiceStatus.Ready;
        public MediaControlAvailability Availability { get; init; } = MediaControlAvailability.Available;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public MediaCommandSubmission TrySubmit(MediaCommand command) =>
            new(MediaCommandSubmissionStatus.Accepted, outcome.OperationId, 1, Task.FromResult(outcome));

        public ValueTask<MediaArtworkContent?> GetArtworkAsync(
            MediaArtworkKey key,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<MediaArtworkContent?>(null);

        public void UpdateOptions(MediaServiceOptions options) { }
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}