// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using JPSoftworks.MediaControlsExtension.Commands;
using JPSoftworks.MediaControlsExtension.Helpers;
using JPSoftworks.MediaControlsExtension.Media;

namespace JPSoftworks.MediaControlsExtension.Presentation.Tests;

[TestClass]
public sealed class MediaCommandFeedbackTests
{
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

        Assert.AreEqual("Playing requested player.", MediaCommandFeedback.AppendPauseWarning("Playing requested player.", skipped));
        Assert.IsNull(MediaCommandFeedback.GetPauseWarning(PartialOutcome() with { Status = MediaCommandOutcomeStatus.Canceled }));
        Assert.IsNull(MediaCommandFeedback.GetPauseWarning(skipped with { PauseOutcomes = [] }));
    }

    [TestMethod]
    public async Task ImmediateFeedbackCanReturnBeforeTheLatePauseWarning()
    {
        var completion = new TaskCompletionSource<MediaCommandOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new List<string>();
        using var notifier = new MediaPauseFailureNotifier(static () => true, messages.Add);
        var observation = notifier.ObserveAsync(completion.Task);

        Assert.IsFalse(observation.IsCompleted);
        Assert.IsEmpty(messages);
        completion.SetResult(PartialOutcome());
        await observation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual("Could not pause another player.", messages.Single());
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task LateWarningsRespectTheToastSettingAtCompletion(bool initialSetting, bool finalSetting)
    {
        var enabled = initialSetting;
        var completion = new TaskCompletionSource<MediaCommandOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new List<string>();
        using var notifier = new MediaPauseFailureNotifier(() => enabled, messages.Add);
        var observation = notifier.ObserveAsync(completion.Task);
        enabled = finalSetting;
        completion.SetResult(PartialOutcome());
        await observation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(finalSetting ? 1 : 0, messages.Count);
    }

    [TestMethod]
    public async Task DisposalStopsLateWarningsWithoutCancelingTheMediaCommand()
    {
        var completion = new TaskCompletionSource<MediaCommandOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new List<string>();
        using var notifier = new MediaPauseFailureNotifier(static () => true, messages.Add);
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
        using var notifier = new MediaPauseFailureNotifier(static () => true, _ =>
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
        using var notifier = new MediaPauseFailureNotifier(static () => true, messages.Add);
        await notifier.ObserveAsync(Task.FromException<MediaCommandOutcome>(new InvalidOperationException("Completion failed")));

        Assert.IsEmpty(messages);
    }

    private static MediaCommandOutcome PartialOutcome() => new(new(1), MediaCommandOutcomeStatus.Completed, new(1), null)
    {
        PauseOutcomes = [new(new(2), 7, MediaCommandOutcomeStatus.Failed, "Pause rejected")],
    };

    private sealed class PlayOperation : MediaSessionOp
    {
        public override MediaOperation Operation => MediaOperation.Play;

        protected override ValueTask<string> GetSuccessMessageAsync(
            IMediaService mediaService, MediaCommandOutcome outcome, CancellationToken cancellationToken) =>
            ValueTask.FromResult("Playing requested player.");

        protected override string GetFailureMessage(object status) => "Primary play failed.";
    }

    private sealed class OutcomeService(MediaCommandOutcome outcome) : IMediaService
    {
        public event EventHandler? SessionsChanged { add { } remove { } }
        public event EventHandler? CurrentSessionChanged { add { } remove { } }
        public event EventHandler? StatusChanged { add { } remove { } }
        public event EventHandler? BackendsChanged { add { } remove { } }
        public ImmutableArray<MediaBackendState> Backends => [];
        public ImmutableArray<MediaSession> Sessions => [];
        public MediaSession? CurrentSession => null;
        public MediaServiceStatus Status => MediaServiceStatus.Ready;
        public MediaControlAvailability Availability => MediaControlAvailability.Available;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public MediaCommandSubmission TrySubmit(MediaCommand command) =>
            new(MediaCommandSubmissionStatus.Accepted, outcome.OperationId, 1, Task.FromResult(outcome));
        public ValueTask<MediaArtworkContent?> GetArtworkAsync(MediaArtworkKey key, CancellationToken cancellationToken) =>
            ValueTask.FromResult<MediaArtworkContent?>(null);
        public void UpdateOptions(MediaServiceOptions options) { }
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}