// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;
using JPSoftworks.MediaControlsExtension.Pages;
using Microsoft.Extensions.Logging.Abstractions;
using static JPSoftworks.MediaControlsExtension.Presentation.Tests.PlaybackPresentationTestSupport;

namespace JPSoftworks.MediaControlsExtension.Presentation.Tests;

[TestClass]
public sealed class WindowsMediaRecoveryItemTests
{
    [TestMethod]
    [DataRow(false, false, true)]
    [DataRow(false, true, false)]
    [DataRow(true, false, false)]
    [DataRow(true, true, false)]
    public async Task RecoveryRequiresNoSessionsAndDisabledWindowsMedia(bool hasSession, bool windowsMediaEnabled, bool expected)
    {
        var backend = new FakeMediaBackend(hasSession ? Snapshot(1, MediaPlaybackState.Paused) : EmptySnapshot(1));
        await using var service = new MediaService(backend);
        await service.StartAsync();
        var enableCalls = 0;
        var item = Item(service, () => windowsMediaEnabled, () => enableCalls++);

        Assert.AreEqual(expected, item.IsNeeded);
        Assert.AreEqual(0, enableCalls);
    }

    [TestMethod]
    public async Task RecoveryWaitsForServiceStartup()
    {
        var backend = new FakeMediaBackend(EmptySnapshot(1));
        backend.BlockStart();
        await using var service = new MediaService(backend);
        var item = Item(service, () => false, () => Assert.Fail("Startup must not enable a source."));
        Assert.IsFalse(item.IsNeeded);
        var startup = service.StartAsync();
        try
        {
            await backend.StartStarted.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(MediaServiceStatus.Starting, service.Status);
            Assert.IsFalse(item.IsNeeded);
            Assert.AreEqual(CommandResultKind.KeepOpen, Invoke(item).Kind);
        }
        finally
        {
            backend.ReleaseStart();
            await startup.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.IsTrue(item.IsNeeded);
    }

    [TestMethod]
    public async Task RecoveryTracksSessionsAppearingAndDisappearing()
    {
        var backend = new FakeMediaBackend(EmptySnapshot(1));
        await using var service = new MediaService(backend);
        await service.StartAsync();
        var item = Item(service, () => false, () => Assert.Fail("State changes must not enable a source."));
        Assert.IsTrue(item.IsNeeded);

        backend.SetSnapshot(Snapshot(2, MediaPlaybackState.Paused));
        await WaitUntilAsync(() => service.Sessions.Length == 1);

        Assert.IsFalse(item.IsNeeded);
        Assert.AreEqual(CommandResultKind.KeepOpen, Invoke(item).Kind);

        backend.SetSnapshot(EmptySnapshot(3));
        await WaitUntilAsync(() => service.Sessions.IsEmpty);

        Assert.IsTrue(item.IsNeeded);
    }

    [TestMethod]
    public async Task RecoveryEnablesOnlyOnInvocationAndKeepsThePageOpen()
    {
        await using var service = new MediaService(new FakeMediaBackend(EmptySnapshot(1)));
        await service.StartAsync();
        var enabled = false;
        var enableCalls = 0;
        var item = Item(service, () => enabled, () =>
        {
            enabled = true;
            enableCalls++;
        });
        Assert.IsTrue(item.IsNeeded);
        Assert.AreEqual(0, enableCalls);

        Assert.AreEqual(CommandResultKind.KeepOpen, Invoke(item).Kind);

        Assert.IsTrue(enabled);
        Assert.AreEqual(1, enableCalls);
        Assert.IsFalse(item.IsNeeded);
        Assert.AreEqual(CommandResultKind.KeepOpen, Invoke(item).Kind);
        Assert.AreEqual(1, enableCalls);
    }

    [TestMethod]
    public async Task StaleRecoveryDoesNotOverrideANewerWindowsMediaChoice()
    {
        await using var service = new MediaService(new FakeMediaBackend(EmptySnapshot(1)));
        await service.StartAsync();
        var enabled = false;
        var enableCalls = 0;
        var item = Item(service, () => enabled, () => enableCalls++);
        Assert.IsTrue(item.IsNeeded);

        enabled = true;

        Assert.AreEqual(CommandResultKind.KeepOpen, Invoke(item).Kind);
        Assert.AreEqual(0, enableCalls);
        Assert.IsFalse(item.IsNeeded);
    }

    [TestMethod]
    public async Task FailedRecoveryKeepsTheChoiceAndReportsTheSaveFailure()
    {
        await using var service = new MediaService(new FakeMediaBackend(EmptySnapshot(1)));
        await service.StartAsync();
        var attempts = 0;
        var item = Item(service, () => false, () =>
        {
            attempts++;
            throw new IOException();
        });

        var result = Invoke(item);

        Assert.AreEqual(CommandResultKind.ShowToast, result.Kind);
        Assert.Contains("Could not save media source settings", Assert.IsInstanceOfType<IToastArgs>(result.Args).Message);
        Assert.IsTrue(item.IsNeeded);
        Assert.AreEqual(1, attempts);
    }

    [TestMethod]
    public void RecoveryOffersConsistentActionsAndABundledSourceIcon()
    {
        using var service = new MediaService(new FakeMediaBackend(EmptySnapshot(1)));
        var sources = new ListPage { Name = "Media sources", Icon = new IconInfo("\uE8F9") };
        var item = new WindowsMediaRecoveryItem(service, () => false, () => { }, sources, NullLoggerFactory.Instance);

        Assert.AreEqual("Windows media is turned off", item.Title);
        Assert.AreEqual("Enable Windows media", item.Command!.Name);
        Assert.EndsWith("Windows.svg", item.Icon!.Light.Icon!);
        Assert.EndsWith("Windows.svg", item.Icon.Dark.Icon!);
        Assert.AreNotEqual(item.Icon.Light.Icon, item.Command.Icon!.Light.Icon);
        Assert.HasCount(1, item.MoreCommands);
        Assert.AreSame(sources, Assert.IsInstanceOfType<ICommandContextItem>(item.MoreCommands[0]).Command);
        var detailsCommands = Assert.IsInstanceOfType<DetailsCommands>(item.Details!.Metadata.Single().Data);
        CollectionAssert.AreEqual(new ICommand[] { item.Command, sources }, detailsCommands.Commands);
    }

    private static WindowsMediaRecoveryItem Item(IMediaService service, Func<bool> isEnabled, Action enable) =>
        new(service, isEnabled, enable, new ListPage { Name = "Media sources" }, NullLoggerFactory.Instance);

    private static ICommandResult Invoke(WindowsMediaRecoveryItem item) =>
        ((IInvokableCommand)item.Command!).Invoke(new ListPage());

    private static MediaBackendSnapshot EmptySnapshot(long revision) => new(revision, [], [], MediaControlAvailability.Available)
    {
        Connection = MediaBackendConnectionState.Connected,
    };
}