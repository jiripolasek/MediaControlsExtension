// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Commands;
using JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;
using JPSoftworks.MediaControlsExtension.Pages;
using Microsoft.Extensions.Logging.Abstractions;
using static JPSoftworks.MediaControlsExtension.Presentation.Tests.PlaybackPresentationTestSupport;

namespace JPSoftworks.MediaControlsExtension.Presentation.Tests;

[TestClass]
public sealed class MediaDetailsPlaybackTests
{
    [TestMethod]
    public async Task PlaybackChangeReplacesDetailsWhilePreviouslyPublishedButtonsRetainTheirAction()
    {
        var backend = new FakeMediaBackend(Snapshot(1, MediaPlaybackState.Playing));
        await using var service = new MediaService(backend);
        await service.StartAsync();
        using var recording = new RecordingPlaybackService(service);
        using var results = new MediaCommandResultFactory(new PlaybackTestSettings(), NullLoggerFactory.Instance);
        using var icons = new PlaybackTestIcons();
        using var viewModel = new MediaSessionViewModel(service, service.CurrentSession!, NullLoggerFactory.Instance);
        var command = new OptimisticPlaybackCommand(recording, results, icons, IconSurface.CommandPalette,
                NullLoggerFactory.Instance)
            .WithPresentation(viewModel.Session);
        var unused = new NoOpCommand();
        var original = new MediaDetails(unused, command, unused, unused, viewModel);
        Assert.IsTrue(original.Represents(viewModel, command));

        backend.SetSnapshot(Snapshot(2, MediaPlaybackState.Paused));
        await WaitUntilAsync(() => viewModel.PlaybackInfo.EffectiveState == MediaPlaybackState.Paused);
        command = command.WithPresentation(viewModel.Session);

        Assert.IsFalse(original.Represents(viewModel, command));
        var replacement = new MediaDetails(unused, command, unused, unused, viewModel);
        var oldButton = PlaybackButton(original);
        var newButton = PlaybackButton(replacement);
        Assert.AreEqual(Strings.Command_Pause, oldButton.Name);
        Assert.AreEqual(Strings.Command_Play, newButton.Name);
        oldButton.Invoke(null!);
        newButton.Invoke(null!);
        CollectionAssert.AreEqual(new[] { MediaOperation.Pause, MediaOperation.Play },
            recording.Commands.Select(static command => command.Operation).ToArray());
    }

    private static IInvokableCommand PlaybackButton(MediaDetails details) =>
        (IInvokableCommand)((DetailsCommands)details.Details.Metadata
            .Single(static element => element.Data is DetailsCommands).Data!).Commands![0];
}