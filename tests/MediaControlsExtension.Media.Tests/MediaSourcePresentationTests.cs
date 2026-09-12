// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Concurrent;
using System.Threading.Channels;
using JPSoftworks.MediaControlsExtension.ViewModels;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class MediaSourcePresentationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExplicitPresentationDoesNotRequireNativeResolution(bool hasNativeIdentity)
    {
        var resolver = new ControlledResolver();
        using var presentation = resolver.CreatePresentation();
        var source = new MediaSourceSnapshot("Music site", "site.png")
        {
            NativeApplication = hasNativeIdentity ? new("browser.app") : null,
            Provider = new("browser", "Browser companion"),
            Details = [new("Profile", "Personal"), new("Page", "My playlist")],
        };

        await presentation.UpdateAsync(source);
        Assert.AreEqual("Music site", await presentation.GetDisplayNameAsync());
        Assert.AreEqual("site.png", presentation.State.IconPath);
        Assert.AreSame(source, presentation.State.Source);
        Assert.AreEqual(0, resolver.CallCount);
    }

    [TestMethod]
    public async Task NativeSourceUsesApplicationIdentityUntilEnrichmentCompletes()
    {
        var resolver = new ControlledResolver();
        using var presentation = resolver.CreatePresentation();
        var notifications = 0;
        presentation.Changed += (_, _) => Interlocked.Increment(ref notifications);
        var update = presentation.UpdateAsync(Native("player.app"));
        var request = await resolver.NextAsync();
        var name = presentation.GetDisplayNameAsync().AsTask();

        Assert.AreEqual("player.app", request.ApplicationId);
        Assert.AreEqual("player.app", presentation.State.DisplayName);
        Assert.IsNull(presentation.State.IconPath);
        Assert.IsFalse(name.IsCompleted);
        request.Complete("Native player", "native.png");
        await update.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual("Native player", await name);
        Assert.AreEqual("native.png", presentation.State.IconPath);
        Assert.AreEqual(1, notifications);
        await presentation.UpdateAsync(Native("player.app"));
        Assert.AreEqual(1, resolver.CallCount);
        Assert.AreEqual(1, notifications);
    }

    [TestMethod]
    [DataRow("Site", null, "Site", "native.png")]
    [DataRow(null, "site.png", "Native player", "site.png")]
    [DataRow(" ", " ", "Native player", "native.png")]
    public async Task NativeEnrichmentFillsOnlyMissingFields(
        string? name, string? icon, string expectedName, string expectedIcon)
    {
        var resolver = new ControlledResolver();
        using var presentation = resolver.CreatePresentation();
        var update = presentation.UpdateAsync(Native("browser.app") with { DisplayName = name, IconPath = icon });
        (await resolver.NextAsync()).Complete("Native player", "native.png");
        await update.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(expectedName, presentation.State.DisplayName);
        Assert.AreEqual(expectedIcon, presentation.State.IconPath);
    }

    [TestMethod]
    public async Task ProviderNameIsTheFallbackForSourcesWithoutAnApplicationOrLabel()
    {
        var resolver = new ControlledResolver();
        using var presentation = resolver.CreatePresentation();
        await presentation.UpdateAsync(new() { Provider = new("remote", "Remote player") });

        Assert.AreEqual("Remote player", await presentation.GetDisplayNameAsync());
        Assert.AreEqual(0, resolver.CallCount);
        await presentation.UpdateAsync(new());
        Assert.AreEqual(string.Empty, presentation.State.DisplayName);
    }

    [TestMethod]
    public async Task NativeResolutionFailureRetainsFallbackAndCompletesWaiters()
    {
        var resolver = new ControlledResolver();
        using var presentation = resolver.CreatePresentation();
        var update = presentation.UpdateAsync(Native("player.app"));
        var request = await resolver.NextAsync();
        var name = presentation.GetDisplayNameAsync().AsTask();
        var failure = new InvalidOperationException("Native lookup failed");
        request.Completion.SetException(failure);
        await update.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual("player.app", await name);
        Assert.AreSame(failure, resolver.Errors.Single());
        Assert.IsNull(presentation.State.IconPath);
    }

    [TestMethod]
    public async Task MetadataUpdatesReleaseNameWaitersAndSurvivePendingIconResolution()
    {
        var resolver = new ControlledResolver();
        using var presentation = resolver.CreatePresentation();
        var initial = Native("browser.app");
        var update = presentation.UpdateAsync(initial);
        var request = await resolver.NextAsync();
        var name = presentation.GetDisplayNameAsync().AsTask();
        var latest = initial with { DisplayName = "New page", Details = [new("Profile", "Work")] };
        var latestUpdate = presentation.UpdateAsync(latest);

        Assert.AreSame(update, latestUpdate);
        Assert.AreEqual("New page", await name.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, resolver.CallCount);
        request.Complete("Browser", "browser.png");
        await update.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual("New page", presentation.State.DisplayName);
        Assert.AreEqual("browser.png", presentation.State.IconPath);
        Assert.AreSame(latest, presentation.State.Source);
    }

    [TestMethod]
    [DataRow("remote")]
    [DataRow("different-application")]
    [DataRow("same-application-again")]
    public async Task ObsoleteNativeResolutionCannotOverwriteReplacementSource(string replacement)
    {
        var resolver = new ControlledResolver();
        using var presentation = resolver.CreatePresentation();
        var initialUpdate = presentation.UpdateAsync(Native("first.app"));
        var initialRequest = await resolver.NextAsync();
        var name = presentation.GetDisplayNameAsync().AsTask();

        Task currentUpdate;
        Lookup? currentRequest = null;
        if (replacement == "remote")
        {
            currentUpdate = presentation.UpdateAsync(new("Remote site", "site.png"));
        }
        else
        {
            if (replacement == "same-application-again")
            {
                await presentation.UpdateAsync(new("Between sources"));
            }

            currentUpdate = presentation.UpdateAsync(Native(
                replacement == "same-application-again" ? "first.app" : "second.app"));
            currentRequest = await resolver.NextAsync();
        }

        initialRequest.Complete("Obsolete name", "obsolete.png");
        await initialUpdate.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreNotEqual("Obsolete name", presentation.State.DisplayName);
        Assert.AreNotEqual("obsolete.png", presentation.State.IconPath);
        currentRequest?.Complete("Replacement", "replacement.png");
        await currentUpdate.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(replacement == "remote" ? "Remote site" : "Replacement",
            await name.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(replacement == "remote" ? "site.png" : "replacement.png", presentation.State.IconPath);
    }

    [TestMethod]
    public async Task DisposalReleasesWaitersAndSuppressesLateResults()
    {
        var resolver = new ControlledResolver();
        using var presentation = resolver.CreatePresentation();
        var notifications = 0;
        presentation.Changed += (_, _) => Interlocked.Increment(ref notifications);
        var update = presentation.UpdateAsync(Native("player.app"));
        var request = await resolver.NextAsync();
        var name = presentation.GetDisplayNameAsync().AsTask();

        presentation.Dispose();
        Assert.AreEqual("player.app", await name.WaitAsync(TimeSpan.FromSeconds(5)));
        request.Complete("Late player", "late.png");
        await update.WaitAsync(TimeSpan.FromSeconds(5));
        await presentation.UpdateAsync(new("After disposal"));

        Assert.AreEqual("player.app", request.ApplicationId);
        Assert.AreEqual("player.app", presentation.State.DisplayName);
        Assert.IsNull(presentation.State.IconPath);
        Assert.AreEqual(0, notifications);
    }

    [TestMethod]
    public async Task CancellingOneNameWaitDoesNotCancelSharedEnrichment()
    {
        var resolver = new ControlledResolver();
        using var presentation = resolver.CreatePresentation();
        var update = presentation.UpdateAsync(Native("player.app"));
        var request = await resolver.NextAsync();
        using var cancellation = new CancellationTokenSource();
        var name = presentation.GetDisplayNameAsync(cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => name);

        request.Complete("Native player", "native.png");
        await update.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("Native player", await presentation.GetDisplayNameAsync());
        Assert.AreEqual(1, resolver.CallCount);
    }

    [TestMethod]
    public async Task FailingPresentationSubscriberDoesNotBlockOtherSubscribers()
    {
        var resolver = new ControlledResolver();
        using var presentation = resolver.CreatePresentation();
        var notified = false;
        presentation.Changed += (_, _) => throw new InvalidOperationException("Subscriber failed");
        presentation.Changed += (_, _) => notified = true;
        var update = presentation.UpdateAsync(Native("player.app"));
        (await resolver.NextAsync()).Complete("Native player", null);
        await update.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(notified);
        Assert.AreEqual("Subscriber failed", resolver.Errors.Single().Message);
    }

    private static MediaSourceSnapshot Native(string applicationId) =>
        new() { NativeApplication = new(applicationId), Provider = new("gsmtc", "Windows media") };

    private sealed class ControlledResolver
    {
        private readonly Channel<Lookup> _requests = Channel.CreateUnbounded<Lookup>();
        private int _callCount;
        public int CallCount => Volatile.Read(ref this._callCount);
        public ConcurrentQueue<Exception> Errors { get; } = new();

        public MediaSourcePresentation CreatePresentation() => new(this.ResolveAsync, this.Errors.Enqueue);

        public async Task<Lookup> NextAsync() =>
            await this._requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        private Task<MediaApplicationPresentation> ResolveAsync(string applicationId)
        {
            Interlocked.Increment(ref this._callCount);
            var request = new Lookup(applicationId);
            this._requests.Writer.TryWrite(request);
            return request.Completion.Task;
        }
    }

    private sealed class Lookup(string applicationId)
    {
        public string ApplicationId { get; } = applicationId;
        public TaskCompletionSource<MediaApplicationPresentation> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(string name, string? icon) => this.Completion.SetResult(new(name, icon));
    }
}