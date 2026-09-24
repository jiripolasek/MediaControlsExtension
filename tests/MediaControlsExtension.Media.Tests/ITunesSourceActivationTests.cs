// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class ITunesSourceActivationTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    public async Task MissingExecutablePathDisablesActivation(string? path)
    {
        var calls = 0;
        await using var fixture = await ITunesNativeFixture.CreateAsync(activateSource: (_, _, _) =>
        {
            calls++;
            return Task.FromResult(true);
        });
        await fixture.SetExecutablePathAsync(path);
        await fixture.RefreshAsync();
        var session = (await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single();

        Assert.IsFalse(session.Capabilities.HasFlag(MediaCapabilities.ActivateSource));
        Assert.AreEqual(MediaBackendCommandStatus.Unsupported, (await fixture.ExecuteAsync(MediaOperation.ActivateSource)).Status);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task KnownDesktopAndStorePathsEnableActivation(bool store)
    {
        string? applicationId = null;
        await using var fixture = await ITunesNativeFixture.CreateAsync(activateSource: (application, _, _) =>
        {
            applicationId = application;
            return Task.FromResult(true);
        });
        var path = store
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps", "AppleInc.iTunes", "iTunes.exe")
            : @"C:\CustomApps\iTunes\iTunes.exe";
        await fixture.SetExecutablePathAsync(path);
        await fixture.RefreshAsync();
        var session = (await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single();

        Assert.IsTrue(session.Capabilities.HasFlag(MediaCapabilities.ActivateSource));
        Assert.AreEqual(path, session.MediaProperties.Source.NativeApplication!.ExecutablePath);
        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await fixture.ExecuteAsync(MediaOperation.ActivateSource)).Status);
        Assert.AreEqual(store ? "AppleInc.iTunes_nzyj5cx40ttqa!iTunes" : "Apple.iTunes", applicationId);
    }

    [TestMethod]
    public async Task PathAvailabilityChangesPublishNewActivationCapabilities()
    {
        await using var fixture = await ITunesNativeFixture.CreateAsync(activateSource: (_, _, _) => Task.FromResult(true));
        await fixture.SetExecutablePathAsync(null);
        await fixture.RefreshAsync();
        var missing = await fixture.Backend.ReadSnapshotAsync(default);

        await fixture.SetExecutablePathAsync(@"C:\CustomApps\iTunes\iTunes.exe");
        await fixture.RefreshAsync();
        var available = await fixture.Backend.ReadSnapshotAsync(default);
        Assert.IsGreaterThan(missing.Revision, available.Revision);
        Assert.IsTrue(available.Sessions.Single().Capabilities.HasFlag(MediaCapabilities.ActivateSource));

        await fixture.SetExecutablePathAsync(null);
        await fixture.RefreshAsync();
        var removed = await fixture.Backend.ReadSnapshotAsync(default);
        Assert.IsGreaterThan(available.Revision, removed.Revision);
        Assert.IsFalse(removed.Sessions.Single().Capabilities.HasFlag(MediaCapabilities.ActivateSource));
        Assert.AreEqual(MediaBackendCommandStatus.Unsupported, (await fixture.ExecuteAsync(MediaOperation.ActivateSource)).Status);
    }

    [TestMethod]
    public async Task ActivationRequiresAHostCallback()
    {
        await using var fixture = await ITunesNativeFixture.CreateAsync();
        await fixture.RefreshAsync();
        var snapshot = await fixture.Backend.ReadSnapshotAsync(default);

        Assert.IsFalse(snapshot.Sessions.Single().Capabilities.HasFlag(MediaCapabilities.ActivateSource));
        Assert.AreEqual(MediaBackendCommandStatus.Unsupported, (await fixture.ExecuteAsync(MediaOperation.ActivateSource)).Status);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ActivationUsesTheCallerContextAndLeavesPlaybackUntouched(bool activated)
    {
        var context = new AsyncLocal<string?> { Value = "admitted command" };
        string? observedContext = null;
        string? applicationId = null;
        string? mediaTitle = null;
        await using var fixture = await ITunesNativeFixture.CreateAsync(activateSource: async (application, title, _) =>
        {
            await Task.Yield();
            observedContext = context.Value;
            applicationId = application;
            mediaTitle = title;
            return activated;
        });
        await fixture.RefreshAsync();
        var before = await fixture.Backend.ReadSnapshotAsync(default);
        var session = before.Sessions.Single();
        Assert.IsTrue(session.Capabilities.HasFlag(MediaCapabilities.ActivateSource));
        fixture.QuitDuring = "PlayerState";

        var result = await fixture.ExecuteAsync(MediaOperation.ActivateSource);

        Assert.AreEqual(activated ? MediaBackendCommandStatus.Completed : MediaBackendCommandStatus.Failed, result.Status);
        Assert.AreEqual(context.Value, observedContext);
        Assert.AreEqual(session.MediaProperties.Source.NativeApplication!.ApplicationId, applicationId);
        Assert.AreEqual(session.MediaProperties.Title, mediaTitle);
        var after = await fixture.Backend.ReadSnapshotAsync(default);
        Assert.AreEqual(before.Revision, after.Revision);
        Assert.AreEqual(session, after.Sessions.Single());
    }

    [TestMethod]
    [DataRow(8)]
    [DataRow(9)]
    public async Task QuitRejectsActivationBeforeDeferredDisconnect(int eventId)
    {
        var calls = 0;
        await using var fixture = await ITunesNativeFixture.CreateAsync(eventId, (_, _, _) =>
        {
            calls++;
            return Task.FromResult(true);
        });
        await fixture.RefreshAsync();
        Task<MediaBackendCommandResult>? activation = null;
        await fixture.RunAsync(() =>
        {
            fixture.SendQuit();
            activation = fixture.ExecuteAsync(MediaOperation.ActivateSource);
        });

        Assert.AreEqual(MediaBackendCommandStatus.Unavailable, (await activation!).Status);
        Assert.AreEqual(0, calls);
        await fixture.AssertQuitCompletedAsync();
    }

    [TestMethod]
    public async Task RemovedAndReplacedSessionsCannotActivate()
    {
        var calls = 0;
        await using var fixture = await ITunesNativeFixture.CreateAsync(activateSource: (_, _, _) =>
        {
            calls++;
            return Task.FromResult(true);
        });
        await fixture.RefreshAsync();
        var session = (await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single();
        var command = new MediaBackendCommand(session.Id, session.BindingGeneration, MediaOperation.ActivateSource, []);
        fixture.HasCurrentTrack = false;
        await fixture.RefreshAsync();
        Assert.AreEqual(MediaBackendCommandStatus.SessionGone, (await fixture.Backend.ExecuteAsync(command, default)).Status);

        fixture.HasCurrentTrack = true;
        await fixture.RefreshAsync();
        Assert.AreEqual(MediaBackendCommandStatus.SessionGone, (await fixture.Backend.ExecuteAsync(command, default)).Status);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task CallbackFailureIsReportedWithoutDisconnecting()
    {
        await using var fixture = await ITunesNativeFixture.CreateAsync(activateSource: (_, _, _) =>
            throw new IOException("Activation failed."));
        await fixture.RefreshAsync();

        var result = await fixture.ExecuteAsync(MediaOperation.ActivateSource);

        Assert.AreEqual(MediaBackendCommandStatus.Failed, result.Status);
        Assert.AreEqual("Activation failed.", result.DiagnosticMessage);
        Assert.AreEqual(MediaBackendConnectionState.Connected, (await fixture.Backend.ReadSnapshotAsync(default)).Connection);
    }

    [TestMethod]
    public async Task ActivationPropagatesCancellationToTheHost()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await ITunesNativeFixture.CreateAsync(activateSource: async (_, _, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return true;
        });
        await fixture.RefreshAsync();
        var session = (await fixture.Backend.ReadSnapshotAsync(default)).Sessions.Single();
        using var cancellation = new CancellationTokenSource();
        var command = new MediaBackendCommand(session.Id, session.BindingGeneration, MediaOperation.ActivateSource, []);
        var activation = fixture.Backend.ExecuteAsync(command, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => activation);
    }
}