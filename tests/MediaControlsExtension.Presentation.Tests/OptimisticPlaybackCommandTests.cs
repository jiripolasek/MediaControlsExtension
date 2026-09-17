// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Commands;
using JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using static JPSoftworks.MediaControlsExtension.Presentation.Tests.PlaybackPresentationTestSupport;

namespace JPSoftworks.MediaControlsExtension.Presentation.Tests;

[TestClass]
public sealed class OptimisticPlaybackCommandTests
{
    [TestMethod]
    [DataRow(MediaCommandOutcomeStatus.Unavailable)]
    [DataRow(MediaCommandOutcomeStatus.Unsupported)]
    [DataRow(MediaCommandOutcomeStatus.Failed)]
    [DataRow(MediaCommandOutcomeStatus.SessionGone)]
    [DataRow(MediaCommandOutcomeStatus.Abandoned)]
    public async Task ImmediatePlaybackFailureDoesNotShowSuccess(MediaCommandOutcomeStatus status)
    {
        var backend = new FakeMediaBackend(Snapshot(1, MediaPlaybackState.Paused));
        await using var service = new MediaService(backend);
        await service.StartAsync();
        using var recording = new RecordingPlaybackService(service)
        {
            ImmediateOutcome = new(new(1), status, new(1), "No command was sent"),
        };
        using var results = new MediaCommandResultFactory(new PlaybackTestSettings(), NullLoggerFactory.Instance);
        using var icons = new PlaybackTestIcons();
        var command = new OptimisticPlaybackCommand(recording, results, icons, IconSurface.CommandPalette,
                NullLoggerFactory.Instance)
            .WithPresentation(service.CurrentSession);

        var result = command.Invoke();

        Assert.AreEqual(
            status switch
            {
                MediaCommandOutcomeStatus.SessionGone => $"\U0001F622 {Strings.Toast_NoCurrentSession}",
                MediaCommandOutcomeStatus.Unsupported => $"\U0001F6AB {Strings.Toast_NothingHappened}",
                MediaCommandOutcomeStatus.Abandoned => "The playback request was canceled because an earlier change could not be confirmed.",
                _ => $"\U0001F622 {Strings.Toast_NothingHappened}",
            },
            ((IToastArgs)result.Args!).Message);
    }

    [TestMethod]
    public async Task ImmediatePauseWarningRetainsThePrimarySuccessMessage()
    {
        var backend = new FakeMediaBackend(Snapshot(1, MediaPlaybackState.Paused));
        await using var service = new MediaService(backend);
        await service.StartAsync();
        using var recording = new RecordingPlaybackService(service)
        {
            ImmediateOutcome = new(new(1), MediaCommandOutcomeStatus.Completed, new(1), null)
            {
                PauseOutcomes = [new(new(2), 1, MediaCommandOutcomeStatus.Failed, "Pause rejected")],
            },
        };
        using var results = new MediaCommandResultFactory(new PlaybackTestSettings(), NullLoggerFactory.Instance);
        using var icons = new PlaybackTestIcons();
        var command = new OptimisticPlaybackCommand(recording, results, icons, IconSurface.CommandPalette,
                NullLoggerFactory.Instance)
            .WithPresentation(service.CurrentSession);

        var result = command.Invoke();

        var message = ((IToastArgs)result.Args!).Message;
        StringAssert.Contains(message, Strings.Toast_Playing);
        StringAssert.Contains(message, "Could not pause another player.");
    }

    [TestMethod]
    public void ToolkitSkipsCommandNotificationsWhenTheSameInstanceIsAssigned()
    {
        var command = new NoOpCommand();
        var item = new ListItem(command);
        var notifications = 0;
        item.PropChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(item.Command))
            {
                notifications++;
            }
        };

        item.Command = command;
        item.Command = command;

        Assert.AreEqual(0, notifications);
    }

    [TestMethod]
    [DataRow(MediaPlaybackState.Playing, MediaPlaybackState.Paused, MediaOperation.Pause)]
    [DataRow(MediaPlaybackState.Paused, MediaPlaybackState.Playing, MediaOperation.Play)]
    public async Task DisplayedActionSurvivesANewerBackendState(
        MediaPlaybackState presented,
        MediaPlaybackState observed,
        MediaOperation expected)
    {
        var backend = new FakeMediaBackend(Snapshot(1, presented));
        await using var service = new MediaService(backend);
        await service.StartAsync();
        using var recording = new RecordingPlaybackService(service);
        using var results = new MediaCommandResultFactory(new PlaybackTestSettings(), NullLoggerFactory.Instance);
        using var icons = new PlaybackTestIcons();
        var command = new OptimisticPlaybackCommand(recording, results, icons, IconSurface.CommandPalette,
            NullLoggerFactory.Instance);
        command = command.WithPresentation(service.CurrentSession);
        var presentation = command.Presentation;

        backend.SetSnapshot(Snapshot(2, observed, "Changed elsewhere"));
        await WaitUntilAsync(() => service.CurrentSession?.MediaProperties.Title == "Changed elsewhere");
        var replacement = command.WithPresentation(service.CurrentSession);
        Assert.AreNotSame(command, replacement);
        Assert.AreEqual(presentation.CommandName, command.Name);
        var result = command.Invoke();

        Assert.AreEqual(expected, recording.Commands.Single().Operation);
        Assert.AreEqual(MediaCommandOutcomeStatus.Completed,
            (await recording.Submissions.Single().Completion!.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.AreEqual(expected, backend.Commands.Single().Operation);
        StringAssert.Contains(((IToastArgs)result.Args!).Message,
            expected == MediaOperation.Play ? Strings.Toast_Playing : Strings.Toast_Paused);
    }

    [TestMethod]
    public async Task RepeatedClicksOnUnchangedPlayDoNotBecomePause()
    {
        var backend = new FakeMediaBackend(Snapshot(1, MediaPlaybackState.Paused));
        backend.BlockCommands();
        await using var service = new MediaService(backend);
        await service.StartAsync();
        using var recording = new RecordingPlaybackService(service);
        using var results = new MediaCommandResultFactory(new PlaybackTestSettings(), NullLoggerFactory.Instance);
        using var icons = new PlaybackTestIcons();
        var command = new OptimisticPlaybackCommand(recording, results, icons, IconSurface.CommandPalette,
            NullLoggerFactory.Instance);
        command = command.WithPresentation(service.CurrentSession);
        try
        {
            command.Invoke();
            await backend.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(MediaPlaybackState.Playing, service.CurrentSession?.PlaybackInfo.EffectiveState);
            command.Invoke();
            command.Invoke();

            CollectionAssert.AreEqual(new[] { MediaOperation.Play, MediaOperation.Play, MediaOperation.Play },
                recording.Commands.Select(static command => command.Operation).ToArray());
            Assert.AreEqual(MediaCommandOutcomeStatus.Superseded, (await recording.Submissions[1].Completion!).Status);
            backend.ReleaseCommands();
            await recording.Submissions[^1].Completion!.WaitAsync(TimeSpan.FromSeconds(5));
            CollectionAssert.AreEqual(new[] { MediaOperation.Play, MediaOperation.Play },
                backend.Commands.Select(static command => command.Operation).ToArray());
        }
        finally
        {
            backend.ReleaseCommands();
        }
    }

    [TestMethod]
    public async Task PublishedCommandRetainsItsTargetAndActionAfterPresentationChanges()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, 1,
            (1, "First", MediaPlaybackState.Paused), (2, "Second", MediaPlaybackState.Playing)));
        await using var service = new MediaService(backend);
        await service.StartAsync();
        using var recording = new RecordingPlaybackService(service);
        using var results = new MediaCommandResultFactory(new PlaybackTestSettings(), NullLoggerFactory.Instance);
        using var icons = new PlaybackTestIcons();
        var command = new OptimisticPlaybackCommand(recording, results, icons, IconSurface.CommandPalette,
            NullLoggerFactory.Instance);
        command = command.WithPresentation(service.Sessions[0]);
        var replacement = command.WithPresentation(service.Sessions[1]);
        Assert.AreEqual(Strings.Command_Pause, replacement.Name);

        var result = command.Invoke();

        Assert.AreEqual(new MediaCommand(MediaCommandTarget.ForSession(service.Sessions[0].Id), MediaOperation.Play),
            recording.Commands.Single());
        StringAssert.Contains(((IToastArgs)result.Args!).Message, Strings.Toast_Playing);
    }

    [TestMethod]
    public async Task FeedbackRetainsTheSubmittedIntentWhenAnotherCommandChangesPrediction()
    {
        var backend = new FakeMediaBackend(Snapshot(1, MediaPlaybackState.Playing));
        backend.BlockCommands();
        await using var service = new MediaService(backend);
        await service.StartAsync();
        using var recording = new RecordingPlaybackService(service)
        {
            AfterSubmit = () => service.TrySubmit(new(MediaCommandTarget.CurrentSession, MediaOperation.Play)),
        };
        using var results = new MediaCommandResultFactory(new PlaybackTestSettings(), NullLoggerFactory.Instance);
        using var icons = new PlaybackTestIcons();
        var command = new OptimisticPlaybackCommand(recording, results, icons, IconSurface.CommandPalette,
            NullLoggerFactory.Instance);
        command = command.WithPresentation(service.CurrentSession);
        try
        {
            var result = command.Invoke();

            Assert.AreEqual(MediaOperation.Pause, recording.Commands.Single().Operation);
            Assert.AreEqual(MediaPlaybackState.Playing, service.CurrentSession?.PlaybackInfo.EffectiveState);
            StringAssert.Contains(((IToastArgs)result.Args!).Message, Strings.Toast_Paused);
        }
        finally
        {
            backend.ReleaseCommands();
        }
    }

    [TestMethod]
    public async Task PresentedStopDoesNotBecomePauseWhenCapabilitiesChange()
    {
        var initial = Snapshot(1, MediaPlaybackState.Playing);
        initial = initial with { Sessions = [initial.Sessions[0] with { Capabilities = MediaCapabilities.Stop }] };
        var backend = new FakeMediaBackend(initial);
        await using var service = new MediaService(backend);
        await service.StartAsync();
        using var recording = new RecordingPlaybackService(service);
        using var results = new MediaCommandResultFactory(new PlaybackTestSettings(), NullLoggerFactory.Instance);
        using var icons = new PlaybackTestIcons();
        var command = new OptimisticPlaybackCommand(recording, results, icons, IconSurface.CommandPalette,
            NullLoggerFactory.Instance);
        command = command.WithPresentation(service.CurrentSession);
        Assert.AreEqual(Strings.Command_Stop, command.Name);

        backend.SetSnapshot(Snapshot(2, MediaPlaybackState.Playing, "Can pause now"));
        await WaitUntilAsync(() => service.CurrentSession?.MediaProperties.Title == "Can pause now");
        var replacement = command.WithPresentation(service.CurrentSession);
        Assert.AreEqual(Strings.Command_Pause, replacement.Name);
        command.Invoke();

        Assert.AreEqual(MediaOperation.Stop, recording.Commands.Single().Operation);
    }

    [TestMethod]
    [DataRow(MediaCapabilities.Play)]
    [DataRow(MediaCapabilities.Play | MediaCapabilities.Stop)]
    public async Task OptimisticPlayOffersDeferredPauseWhileCapabilitiesLag(MediaCapabilities capabilities)
    {
        var snapshot = Snapshot(1, MediaPlaybackState.Paused);
        snapshot = snapshot with
        {
            Sessions =
            [
                snapshot.Sessions[0] with { Capabilities = capabilities }
            ]
        };
        var backend = new FakeMediaBackend(snapshot);
        backend.BlockCommands();
        await using var service = new MediaService(backend);
        await service.StartAsync();
        using var recording = new RecordingPlaybackService(service);
        using var results = new MediaCommandResultFactory(new PlaybackTestSettings(), NullLoggerFactory.Instance);
        using var icons = new PlaybackTestIcons();
        var command = new OptimisticPlaybackCommand(recording, results, icons, IconSurface.CommandPalette,
                NullLoggerFactory.Instance)
            .WithPresentation(service.CurrentSession);
        try
        {
            command.Invoke();
            await backend.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
            command = command.WithPresentation(service.CurrentSession);
            Assert.AreEqual(Strings.Command_Pause, command.Name);
            var result = command.Invoke();

            Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, recording.Submissions[1].Status);
            Assert.AreEqual(MediaOperation.Pause, recording.Commands[1].Operation);
            StringAssert.Contains(((IToastArgs)result.Args!).Message, Strings.Toast_Paused);
            backend.ReleaseCommands();
            await recording.Submissions[1].Completion!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(MediaOperation.Pause, backend.Commands[1].Operation);
        }
        finally
        {
            backend.ReleaseCommands();
        }
    }

    [TestMethod]
    public async Task ConfirmedStopOnlyPlaybackOffersStopAfterThePredictionClears()
    {
        var initial = Snapshot(1, MediaPlaybackState.Paused);
        initial = initial with { Sessions = [initial.Sessions[0] with { Capabilities = MediaCapabilities.Play | MediaCapabilities.Stop }] };
        var backend = new FakeMediaBackend(initial);
        backend.BlockCommands();
        await using var service = new MediaService(backend);
        await service.StartAsync();
        using var results = new MediaCommandResultFactory(new PlaybackTestSettings(), NullLoggerFactory.Instance);
        using var icons = new PlaybackTestIcons();
        var command = new OptimisticPlaybackCommand(service, results, icons, IconSurface.CommandPalette,
            NullLoggerFactory.Instance);
        try
        {
            var play = service.TrySubmit(new(MediaCommandTarget.CurrentSession, MediaOperation.Play));
            await backend.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
            var confirmed = Snapshot(2, MediaPlaybackState.Playing);
            backend.SetSnapshot(confirmed with { Sessions = [confirmed.Sessions[0] with { Capabilities = MediaCapabilities.Stop }] });
            await WaitUntilAsync(() => service.CurrentSession?.PlaybackInfo.ConfirmedState == MediaPlaybackState.Playing);
            Assert.AreEqual(Strings.Command_Pause, command.WithPresentation(service.CurrentSession).Name);

            backend.ReleaseCommands();
            await play.Completion!.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.IsFalse(service.CurrentSession!.PlaybackInfo.IsOptimistic);
            Assert.AreEqual(Strings.Command_Stop, command.WithPresentation(service.CurrentSession).Name);
        }
        finally
        {
            backend.ReleaseCommands();
        }
    }

    [TestMethod]
    public async Task MetadataUpdatesReuseThePublishedCommand()
    {
        var backend = new FakeMediaBackend(Snapshot(1, MediaPlaybackState.Playing));
        await using var service = new MediaService(backend);
        await service.StartAsync();
        using var results = new MediaCommandResultFactory(new PlaybackTestSettings(), NullLoggerFactory.Instance);
        using var icons = new PlaybackTestIcons();
        var command = new OptimisticPlaybackCommand(service, results, icons, IconSurface.CommandPalette,
                NullLoggerFactory.Instance)
            .WithPresentation(service.CurrentSession);

        backend.SetSnapshot(Snapshot(2, MediaPlaybackState.Playing, "New title"));
        await WaitUntilAsync(() => service.CurrentSession?.MediaProperties.Title == "New title");

        Assert.AreSame(command, command.WithPresentation(service.CurrentSession));
    }

    [TestMethod]
    public async Task DisablingTheOwnerDisablesPreviouslyPublishedCommands()
    {
        var backend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, 1,
            (1, "First", MediaPlaybackState.Playing), (2, "Second", MediaPlaybackState.Paused)));
        await using var service = new MediaService(backend);
        await service.StartAsync();
        using var recording = new RecordingPlaybackService(service);
        using var results = new MediaCommandResultFactory(new PlaybackTestSettings(), NullLoggerFactory.Instance);
        using var icons = new PlaybackTestIcons();
        var original = new OptimisticPlaybackCommand(recording, results, icons, IconSurface.CommandPalette,
                NullLoggerFactory.Instance)
            .WithPresentation(service.Sessions[0]);
        var replacement = original.WithPresentation(service.Sessions[1]);

        replacement.Disable();
        original.Invoke();
        replacement.Invoke();

        Assert.IsEmpty(recording.Commands);
    }
}