// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using JPSoftworks.MediaControlsExtension.Media.Browser;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class BrowserBackendTests
{
    private static readonly byte[] ArtworkPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg==");

    [TestMethod]
    public async Task SiteNameAndFaviconsReachThePresentationContractsAndAreCached()
    {
        await using var server = new CompanionServer();
        var requests = new ConcurrentBag<string>();
        var images = new BrowserImageLoader(new ImageHandler((request, _) =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(ImageResponse());
        }));
        await using var backend = new BrowserBackend(server.PipeName, images, "browser.svg", recoveryDelay: TimeSpan.Zero);
        await backend.StartAsync(default);
        var connection = Connection("edge", "profile");
        connection["sessions"]![0]!["artwork"] = "https://media.test/cover.png";
        connection["sessions"]![0]!["favicon"] = "https://media.test/favicon.png";
        connection["sessions"]![0]!["page"] = "https://media.test/watch";
        connection["sessions"]![0]!["siteName"] = "YouTube";
        await server.PublishAsync(Catalog(1, connection));
        var session = (await UntilAsync(backend, value => value.Sessions.Length == 1 &&
            value.Sessions[0].MediaProperties.Source.IconPath is not (null or "browser.svg"))).Sessions[0];
        var key = session.MediaProperties.Artwork!.Value;
        var art = await backend.GetArtworkAsync(key, default);
        Assert.IsNotNull(art);
        Assert.AreEqual("image/png", art.ContentType);
        CollectionAssert.AreEqual(ArtworkPng, art.Data.ToArray());
        Assert.AreSame(art, await backend.GetArtworkAsync(key, default));
        CollectionAssert.AreEqual(ArtworkPng, await File.ReadAllBytesAsync(session.MediaProperties.Source.IconPath!));
        Assert.AreEqual("YouTube", session.MediaProperties.Source.DisplayName);
        Assert.AreEqual("https://media.test/watch", session.MediaProperties.Source.Details.Last().Value);
        Assert.AreEqual(1, requests.Count(path => path == "/cover.png"));
        Assert.AreEqual(1, requests.Count(path => path == "/favicon.png"));
        await backend.DisposeAsync();
        Assert.IsFalse(File.Exists(session.MediaProperties.Source.IconPath));
        Assert.IsNull(await backend.GetArtworkAsync(key, default));
    }

    [TestMethod]
    [DataRow("  YouTube  ", "https://www.youtube.com/watch", "https://player.test/embed", "YouTube")]
    [DataRow(null, "https://www.site.test/watch", "https://player.test/embed", "site.test")]
    [DataRow(" ", null, "https://www.media.test/watch", "media.test")]
    [DataRow(null, null, null, "Edge")]
    [DataRow(null, "file:///private.mp4", null, "Edge")]
    [DataRow(null, "https://user:secret@site.test/", null, "Edge")]
    public void SourceTagUsesSiteMetadataAndFallsBackToHostOrBrowser(string? siteName, string? tabUrl, string? page, string expected)
    {
        var connection = Connection("edge", "7a89a36e-full-profile");
        connection["browser"] = "Edge";
        var session = connection["sessions"]![0]!;
        session["siteName"] = siteName;
        session["tabUrl"] = tabUrl;
        session["page"] = page;
        var catalog = BrowserCatalog.Parse(JsonSerializer.SerializeToElement(Catalog(1, connection)));
        var target = catalog.Targets.Single();
        Assert.AreEqual(expected, target.Properties.Source.DisplayName);
        Assert.AreEqual("Edge", target.Properties.Source.Details.First().Value);
        Assert.AreEqual("Edge (7a89a36e)", catalog.Health.Single().DisplayName);
        Assert.AreEqual("MSEdge:7a89a36e-full-profile", target.Origin);
        Assert.AreEqual("doc:player", target.SessionId);
    }

    [TestMethod]
    public async Task LateCoverCannotCrossATrackChangeAndDoesNotBlockPlayback()
    {
        await using var server = new CompanionServer();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var images = new BrowserImageLoader(new ImageHandler(async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return ImageResponse();
        }));
        await using var backend = new BrowserBackend(server.PipeName, images, "browser.svg", recoveryDelay: TimeSpan.Zero);
        await backend.StartAsync(default);
        var connection = Connection("edge", "profile");
        connection["sessions"]![0]!["artwork"] = "https://media.test/current.png";
        await server.PublishAsync(Catalog(1, connection));
        var first = (await UntilAsync(backend, value => value.Sessions.Length == 1)).Sessions[0];
        var oldKey = first.MediaProperties.Artwork!.Value;
        var pending = backend.GetArtworkAsync(oldKey, default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await backend.ExecuteAsync(Command(first), default)).Status);
        var replacement = (JsonObject)connection.DeepClone();
        replacement["sessions"]![0]!["title"] = "Next track, same image URL";
        await server.PublishAsync(Catalog(2, replacement));
        var second = (await UntilAsync(backend, value => value.Sessions[0].MediaProperties.Artwork != oldKey)).Sessions[0];
        release.TrySetResult();
        Assert.IsNull(await pending);
        Assert.IsNull(await backend.GetArtworkAsync(oldKey, default));
        Assert.AreEqual(first.BindingGeneration, second.BindingGeneration);
        Assert.IsNotNull(await backend.GetArtworkAsync(second.MediaProperties.Artwork!.Value, default));
        var cleared = (JsonObject)replacement.DeepClone();
        cleared["sessions"]![0]!["artwork"] = "";
        await server.PublishAsync(Catalog(3, cleared));
        await UntilAsync(backend, value => value.Sessions[0].MediaProperties.Artwork is null);
        Assert.IsNull(await backend.GetArtworkAsync(second.MediaProperties.Artwork.Value, default));
    }

    [TestMethod]
    public async Task LateFaviconDoesNotReplaceANewerTabsIconAndFailureKeepsTheFallback()
    {
        await using var server = new CompanionServer();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var images = new BrowserImageLoader(new ImageHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/old.png")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return ImageResponse();
            }
            return new(HttpStatusCode.NotFound);
        }));
        await using var backend = new BrowserBackend(server.PipeName, images, "browser.svg", recoveryDelay: TimeSpan.Zero);
        await backend.StartAsync(default);
        var connection = Connection("edge", "profile");
        connection["sessions"]![0]!["favicon"] = "https://media.test/old.png";
        connection["browser"] = "Edge";
        connection["sessions"]![0]!["siteName"] = "YouTube";
        await server.PublishAsync(Catalog(1, connection));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var changed = (JsonObject)connection.DeepClone();
        changed["sessions"]![0]!["favicon"] = "https://media.test/missing.png";
        changed["sessions"]![0]!["title"] = "New page";
        await server.PublishAsync(Catalog(2, changed));
        await UntilAsync(backend, value => value.Sessions[0].MediaProperties.Title == "New page");
        release.TrySetResult();
        await Task.Delay(100);
        var source = (await backend.ReadSnapshotAsync(default)).Sessions[0].MediaProperties.Source;
        Assert.AreEqual("browser.svg", source.IconPath);
        Assert.AreEqual("YouTube", source.DisplayName);
    }

    [TestMethod]
    public async Task ImageLoaderRejectsUnsafeUrlsRedirectsOversizedBodiesAndNonImages()
    {
        var requests = 0;
        using var images = new BrowserImageLoader(new ImageHandler((request, _) =>
        {
            requests++;
            return Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/redirect" => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("file:///private.png") } },
                "/large" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[BrowserImageLoader.MaximumBytes + 1]) },
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<svg><script/></svg>") }
            });
        }));
        foreach (var url in new[] { "file:///private.png", "https://user:secret@media.test/private.png", "data:image/png;base64,eA==" })
            Assert.IsNull(await images.LoadAsync(url, false, default));
        Assert.AreEqual(0, requests);
        foreach (var path in new[] { "redirect", "large", "fake.png" })
            Assert.IsNull(await images.LoadAsync("https://media.test/" + path, false, default));
        Assert.AreEqual(3, requests);
    }

    [TestMethod]
    [DataRow("000000146674797061766966000000006D696631", DisplayName = "AVIF major brand")]
    [DataRow("00000014667479706D6966310000000061766966", DisplayName = "AVIF compatible brand")]
    [DataRow("000000146674797061766973000000006D736631", DisplayName = "AVIF sequence brand")]
    public async Task ImageLoaderRecognizesAvifFileTypeBrands(string hex)
    {
        var data = Convert.FromHexString(hex);
        using var images = new BrowserImageLoader(new ImageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
            response.Content.Headers.ContentType = new("image/jpeg");
            return Task.FromResult(response);
        }));
        var content = await images.LoadAsync("https://media.test/cover.jpg", false, default);
        Assert.IsNotNull(content);
        Assert.AreEqual("image/avif", content.ContentType);
        CollectionAssert.AreEqual(data, content.Data.ToArray());
        var path = await images.LoadIconAsync("https://media.test/icon.jpg", default);
        Assert.IsNotNull(path);
        Assert.AreEqual(".avif", Path.GetExtension(path));
        CollectionAssert.AreEqual(data, await File.ReadAllBytesAsync(path));
    }

    [TestMethod]
    [DataRow("000000106674797061766966000000", DisplayName = "Truncated AVIF header")]
    [DataRow("0000000C667479706176696600000000", DisplayName = "Undersized file type box")]
    [DataRow("00000018667479706176696600000000", DisplayName = "File type box exceeds response")]
    [DataRow("0000001166747970617669660000000000", DisplayName = "Misaligned file type box")]
    [DataRow("000000106674797069736F6D61766966", DisplayName = "AVIF text in minor version")]
    [DataRow("000000106674797069736F6D0000000061766966", DisplayName = "AVIF text outside file type box")]
    [DataRow("000000146674797069736F6D000000006D703432", DisplayName = "MP4 brands")]
    [DataRow("00000014667479706D6966310000000068656963", DisplayName = "HEIC brands")]
    public async Task ImageLoaderRejectsMalformedOrNonAvifFileTypes(string hex)
    {
        using var images = new BrowserImageLoader(new ImageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Convert.FromHexString(hex)) };
            response.Content.Headers.ContentType = new("image/avif");
            return Task.FromResult(response);
        }));
        Assert.IsNull(await images.LoadAsync("https://media.test/cover.avif", false, default));
    }

    private static HttpResponseMessage ImageResponse() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(ArtworkPng) };

    private sealed class ImageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    [TestMethod]
    public async Task ProfilesWithTheSameTabIdRemainIndependentAndCommandsCaptureThePrimary()
    {
        await using var server = new CompanionServer();
        await using var backend = new BrowserBackend(server.PipeName, recoveryDelay: TimeSpan.Zero);
        await backend.StartAsync(default);
        await using var signals = backend.WatchAsync(default).GetAsyncEnumerator();
        Assert.IsTrue(await signals.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        var empty = await UntilAsync(backend, snapshot => snapshot.Revision > 0);
        Assert.IsEmpty(empty.Sessions);
        Assert.IsEmpty(empty.ReplacesSources);
        Assert.AreEqual(MediaControlAvailability.Unavailable, empty.Availability);

        await server.PublishAsync(Catalog(1, Connection("edge", "profile-a"), Connection("chrome", "profile-b")));
        var snapshot = await UntilAsync(backend, value => value.Sessions.Length == 2);
        var first = snapshot.Sessions[0];
        var second = snapshot.Sessions[1];
        Assert.AreNotEqual(first.Id, second.Id);
        Assert.AreNotEqual(first.Origin, second.Origin);
        Assert.HasCount(2, snapshot.Connection.Connections);
        Assert.IsTrue(first.Origin.TreatAsLocal);
        Assert.IsNull(first.MediaProperties.Source.NativeApplication);
        Assert.AreEqual(TimeSpan.FromSeconds(120), first.TimelineProperties.Duration);
        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await backend.ExecuteAsync(Command(first), default)).Status);
        var sent = await server.Commands.Reader.ReadAsync();
        Assert.AreEqual("edge", sent.GetProperty("connectionId").GetString());
        Assert.AreEqual("doc:player", sent.GetProperty("sessionId").GetString());
        Assert.AreEqual(7, sent.GetProperty("tabId").GetInt32());
        Assert.AreEqual(1, sent.GetProperty("generation").GetInt64());
        Assert.AreEqual("pause", sent.GetProperty("operation").GetString());
        Assert.IsTrue(sent.GetProperty("deadline").GetInt64() <= DateTimeOffset.UtcNow.AddSeconds(3).ToUnixTimeMilliseconds());
        Assert.AreEqual(MediaBackendCommandStatus.Unsupported,
            (await backend.ExecuteAsync(Command(first) with { Operation = MediaOperation.SkipNext }, default)).Status);
        Assert.IsFalse(server.Commands.Reader.TryRead(out _));

        await server.PublishAsync(Catalog(2, Connection("edge", "profile-a", 2, "doc:replacement"), Connection("chrome", "profile-b")));
        var rebound = await UntilAsync(backend, value => value.Sessions[0].BindingGeneration != first.BindingGeneration);
        Assert.AreEqual(first.Id, rebound.Sessions[0].Id);
        Assert.AreEqual(second.BindingGeneration, rebound.Sessions[1].BindingGeneration);
        Assert.AreEqual(MediaBackendCommandStatus.SessionGone, (await backend.ExecuteAsync(Command(first), default)).Status);
        Assert.IsFalse(server.Commands.Reader.TryRead(out _));
        Assert.AreEqual(MediaBackendCommandStatus.Completed,
            (await backend.ExecuteAsync(Command(rebound.Sessions[0]) with { Operation = MediaOperation.ActivateSource }, default)).Status);
        sent = await server.Commands.Reader.ReadAsync();
        Assert.AreEqual("doc:replacement", sent.GetProperty("sessionId").GetString());
        Assert.AreEqual("activate", sent.GetProperty("operation").GetString());
    }

    [TestMethod]
    [DataRow(MediaOperation.Play, true)]
    [DataRow(MediaOperation.Play, false)]
    [DataRow(MediaOperation.ActivateSource, true)]
    [DataRow(MediaOperation.Pause, true)]
    public async Task OnlyPlayAndActivationTransferForegroundToTheConnectedRelay(MediaOperation operation, bool granted)
    {
        await using var server = new CompanionServer();
        var requests = 0;
        await using var backend = new BrowserBackend(server.PipeName, new BrowserImageLoader(), null, pipe =>
        {
            Assert.IsTrue(pipe.IsConnected);
            requests++;
            return granted;
        }, recoveryDelay: TimeSpan.Zero);
        await backend.StartAsync(default);
        await server.PublishAsync(Catalog(1, Connection("edge", "profile")));
        var session = (await UntilAsync(backend, value => value.Sessions.Length == 1)).Sessions[0];
        var command = new MediaBackendCommand(session.Id, session.BindingGeneration, operation, []);
        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await backend.ExecuteAsync(command, default)).Status);
        var sent = await server.Commands.Reader.ReadAsync();
        var needsForeground = operation is MediaOperation.Play or MediaOperation.ActivateSource;
        Assert.AreEqual(needsForeground ? 1 : 0, requests);
        Assert.AreEqual(needsForeground && granted, sent.GetProperty("allowForeground").GetBoolean());
        Assert.AreEqual(MediaBackendCommandStatus.Unsupported,
            (await backend.ExecuteAsync(command with { Operation = MediaOperation.SkipNext }, default)).Status);
        Assert.AreEqual(MediaBackendCommandStatus.SessionGone,
            (await backend.ExecuteAsync(command with { BindingGeneration = session.BindingGeneration + 1 }, default)).Status);
        Assert.AreEqual(needsForeground ? 1 : 0, requests, "Rejected commands must not transfer foreground permission");
        Assert.IsFalse(server.Commands.Reader.TryRead(out _));
    }

    [TestMethod]
    public async Task ServiceRestartWithdrawsTargetsAndNeverReusesTheirIdentities()
    {
        var server = new CompanionServer();
        await using var backend = new BrowserBackend(server.PipeName, recoveryDelay: TimeSpan.Zero);
        try
        {
            await backend.StartAsync(default);
            await server.PublishAsync(Catalog(4, Connection("old", "profile")));
            var before = await UntilAsync(backend, value => value.Sessions.Length == 1);
            await server.DisposeAsync();
            var gone = await UntilAsync(backend, value => value.Connection.Status == MediaConnectionStatus.Connecting);
            Assert.IsEmpty(gone.Sessions);
            Assert.HasCount(2, gone.ReplacesSources);
            Assert.IsTrue(gone.Revision > before.Revision);
            Assert.AreEqual(MediaBackendCommandStatus.SessionGone, (await backend.ExecuteAsync(Command(before.Sessions[0]), default)).Status);
            await using var restarted = new CompanionServer(server.PipeName);
            await restarted.PublishAsync(Catalog(1, Connection("new", "profile")));
            var after = await UntilAsync(backend, value => value.Sessions.Length == 1);
            Assert.AreNotEqual(before.Sessions[0].Id, after.Sessions[0].Id);
            Assert.AreEqual(before.Sessions[0].Origin, after.Sessions[0].Origin);
            Assert.IsTrue(after.Revision > gone.Revision);
            Assert.AreEqual(MediaBackendCommandStatus.SessionGone, (await backend.ExecuteAsync(Command(before.Sessions[0]), default)).Status);
        }
        finally { await server.DisposeAsync(); }
    }

    [TestMethod]
    public async Task MetadataUpdatesKeepBindingsButRemovedTabsGetFreshIdentities()
    {
        await using var server = new CompanionServer();
        await using var backend = new BrowserBackend(server.PipeName, recoveryDelay: TimeSpan.Zero);
        await backend.StartAsync(default);
        await server.PublishAsync(Catalog(1, Connection("edge", "profile")));
        var before = (await UntilAsync(backend, value => value.Sessions.Length == 1)).Sessions[0];
        var changed = Connection("edge", "profile");
        changed["sessions"]![0]!["title"] = "New title";
        await server.PublishAsync(Catalog(2, changed));
        var after = (await UntilAsync(backend, value => value.Sessions[0].MediaProperties.Title == "New title")).Sessions[0];
        Assert.AreEqual(before.Id, after.Id);
        Assert.AreEqual(before.BindingGeneration, after.BindingGeneration);
        await server.PublishAsync(Catalog(3));
        await UntilAsync(backend, value => value.Sessions.IsEmpty);
        await server.PublishAsync(Catalog(4, Connection("edge", "profile")));
        after = (await UntilAsync(backend, value => value.Sessions.Length == 1)).Sessions[0];
        Assert.AreNotEqual(before.Id, after.Id);
    }

    [TestMethod]
    public async Task ACommandWithNoResultIsUnconfirmedAndIsNeverRetried()
    {
        await using var server = new CompanionServer { CommandResult = null };
        await using var backend = new BrowserBackend(server.PipeName, recoveryDelay: TimeSpan.Zero);
        await backend.StartAsync(default);
        await server.PublishAsync(Catalog(1, Connection("edge", "profile")));
        var session = (await UntilAsync(backend, value => value.Sessions.Length == 1)).Sessions[0];
        var result = await backend.ExecuteAsync(Command(session), default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(MediaBackendCommandStatus.Unconfirmed, result.Status);
        Assert.IsTrue(server.Commands.Reader.TryRead(out _));
        Assert.IsFalse(server.Commands.Reader.TryRead(out _));
    }

    [TestMethod]
    public async Task CancellationDrainsAnInFlightCommandAndDisposalStopsMonitoring()
    {
        await using var server = new CompanionServer { CommandResult = null };
        await using var backend = new BrowserBackend(server.PipeName, recoveryDelay: TimeSpan.Zero);
        await backend.StartAsync(default);
        await server.PublishAsync(Catalog(1, Connection("edge", "profile")));
        var session = (await UntilAsync(backend, value => value.Sessions.Length == 1)).Sessions[0];
        using var cancellation = new CancellationTokenSource();
        var pending = backend.ExecuteAsync(Command(session), cancellation.Token);
        await server.Commands.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        await backend.DisposeAsync();
        Assert.IsEmpty((await backend.ReadSnapshotAsync(default)).Sessions);
        Assert.AreEqual(MediaBackendCommandStatus.SessionGone, (await backend.ExecuteAsync(Command(session), default)).Status);
    }

    [TestMethod]
    public async Task MalformedCatalogWithdrawsAllTargetsInsteadOfPublishingAPartialUpdate()
    {
        await using var server = new CompanionServer();
        await using var backend = new BrowserBackend(server.PipeName, recoveryDelay: TimeSpan.Zero);
        await backend.StartAsync(default);
        await server.PublishAsync(Catalog(1, Connection("edge", "profile")));
        await UntilAsync(backend, value => value.Sessions.Length == 1);
        var invalid = Connection("other", "profile-b");
        invalid["tabs"]![0]!["sessionId"] = "missing";
        await server.PublishAsync(Catalog(2, Connection("edge", "profile"), invalid));
        var snapshot = await UntilAsync(backend, value => value.Connection.Status == MediaConnectionStatus.Connecting);
        Assert.IsEmpty(snapshot.Sessions);
        Assert.HasCount(2, snapshot.ReplacesSources);
        Assert.AreEqual(MediaControlAvailability.Unavailable, snapshot.Availability);
    }

    [TestMethod]
    [DataRow("sleeping")]
    [DataRow("unresponsive")]
    public async Task SleepingTabsCanWakeForPlayWhileUnresponsiveTabsOnlyActivate(string browserState)
    {
        await using var server = new CompanionServer();
        await using var backend = new BrowserBackend(server.PipeName, recoveryDelay: TimeSpan.Zero);
        await backend.StartAsync(default);
        var connection = Connection("edge", "profile");
        connection["sessions"]![0]!["state"] = browserState;
        await server.PublishAsync(Catalog(1, connection));
        var sleeping = (await UntilAsync(backend, value => value.Sessions.Length == 1)).Sessions[0];
        var canWake = browserState == "sleeping";
        Assert.AreEqual(MediaCapabilities.ActivateSource | (canWake ? MediaCapabilities.Play : MediaCapabilities.None), sleeping.Capabilities);
        Assert.AreEqual(MediaPlaybackState.Unknown, sleeping.PlaybackState);
        Assert.IsTrue(sleeping.MediaProperties.Source.Details.Any(detail => detail.Value.Contains("Switch to application", StringComparison.Ordinal)));
        Assert.AreEqual(MediaBackendCommandStatus.Unsupported, (await backend.ExecuteAsync(Command(sleeping), default)).Status);
        Assert.AreEqual(canWake ? MediaBackendCommandStatus.Completed : MediaBackendCommandStatus.Unsupported,
            (await backend.ExecuteAsync(new(sleeping.Id, sleeping.BindingGeneration, MediaOperation.Play, []), default)).Status);
        if (canWake)
        {
            var play = await server.Commands.Reader.ReadAsync();
            Assert.AreEqual("play", play.GetProperty("operation").GetString());
            Assert.AreEqual("doc:player", play.GetProperty("sessionId").GetString());
        }
        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await backend.ExecuteAsync(
            new(sleeping.Id, sleeping.BindingGeneration, MediaOperation.ActivateSource, []), default)).Status);
        var activation = await server.Commands.Reader.ReadAsync();
        Assert.AreEqual("activate", activation.GetProperty("operation").GetString());
        Assert.IsFalse(server.Commands.Reader.TryRead(out _));

        await server.PublishAsync(Catalog(2, Connection("edge", "profile")));
        var awake = (await UntilAsync(backend, value => value.Sessions[0].PlaybackState == MediaPlaybackState.Playing)).Sessions[0];
        Assert.AreEqual(sleeping.Id, awake.Id);
        Assert.AreEqual(sleeping.BindingGeneration, awake.BindingGeneration);
        Assert.IsTrue(awake.Capabilities.HasFlag(MediaCapabilities.Play));
    }

    private static MediaBackendCommand Command(MediaBackendSessionSnapshot session) => new(session.Id, session.BindingGeneration, MediaOperation.Pause, []);

    [TestMethod]
    public async Task HeartbeatsWithUnchangedCatalogRevisionRecoverAndWithdrawBindings()
    {
        await using var server = new CompanionServer();
        await using var backend = new BrowserBackend(server.PipeName, recoveryDelay: TimeSpan.FromMilliseconds(100));
        await backend.StartAsync(default);
        await server.PublishAsync(Catalog(1, Connection("edge", "profile")));
        await UntilAsync(backend, value => value.Connection.Connections.Length == 1);
        Assert.IsEmpty((await backend.ReadSnapshotAsync(default)).Sessions);
        await Task.Delay(150);
        await server.PublishAsync(Catalog(1, Connection("edge", "profile")));
        var before = (await UntilAsync(backend, value => value.Sessions.Length == 1)).Sessions[0];
        var stale = Connection("edge", "profile");
        stale["heartbeatAgeMs"] = 20000;
        await server.PublishAsync(Catalog(1, stale));
        var lost = await UntilAsync(backend, value => value.Sessions.IsEmpty);
        Assert.HasCount(2, lost.ReplacesSources);
        Assert.AreEqual(MediaBackendCommandStatus.SessionGone, (await backend.ExecuteAsync(Command(before), default)).Status);
        await server.PublishAsync(Catalog(1, Connection("edge", "profile")));
        var after = (await UntilAsync(backend, value => value.Sessions.Length == 1)).Sessions[0];
        Assert.AreNotEqual(before.Id, after.Id);
        Assert.IsFalse(server.Commands.Reader.TryRead(out _));
    }

    [TestMethod]
    public async Task HealthTimerReleasesClaimsWithoutFurtherRelayMessages()
    {
        var clock = new HeartbeatClock();
        await using var server = new CompanionServer();
        await using var backend = new BrowserBackend(server.PipeName, new BrowserImageLoader(), null,
            recoveryDelay: TimeSpan.Zero, clock: clock);
        await backend.StartAsync(default);
        await server.PublishAsync(Catalog(1, Connection("edge", "profile")));
        var original = (await UntilAsync(backend, value => value.Sessions.Length == 1)).Sessions[0];
        for (var second = 1; second <= 60; second++)
        {
            await clock.AdvanceAsync(TimeSpan.FromSeconds(1));
            var snapshot = await backend.ReadSnapshotAsync(default);
            Assert.HasCount(second < 20 ? 1 : 0, snapshot.Sessions);
            Assert.HasCount(second < 60 ? 2 : 0, snapshot.ReplacesSources);
        }
        Assert.AreEqual(MediaBackendCommandStatus.SessionGone, (await backend.ExecuteAsync(Command(original), default)).Status);
        Assert.IsFalse(server.Commands.Reader.TryRead(out _));
    }

    private sealed class HeartbeatClock : TimeProvider
    {
        private readonly TaskCompletionSource<HeartbeatTimer> _timer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _observed;
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            var timestamp = Interlocked.Read(ref this._timestamp);
            Interlocked.Exchange(ref this._observed, null)?.TrySetResult();
            return timestamp;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.AreEqual(TimeSpan.FromSeconds(1), period);
            Assert.AreEqual(period, dueTime);
            var timer = new HeartbeatTimer(callback, state);
            this._timer.TrySetResult(timer);
            return timer;
        }

        internal async Task AdvanceAsync(TimeSpan elapsed)
        {
            var timer = await this._timer.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            this._observed = observed;
            Interlocked.Add(ref this._timestamp, elapsed.Ticks);
            timer.Tick();
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        private sealed class HeartbeatTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            internal void Tick() { if (!this._disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !this._disposed;
            public void Dispose() => this._disposed = true;
            public ValueTask DisposeAsync() { this.Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static async Task<MediaBackendSnapshot> UntilAsync(BrowserBackend backend, Func<MediaBackendSnapshot, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (true)
        {
            var snapshot = await backend.ReadSnapshotAsync(timeout.Token);
            if (predicate(snapshot)) return snapshot;
            await Task.Delay(10, timeout.Token);
        }
    }

    private static JsonObject Catalog(long revision, params JsonObject[] connections) => new()
    {
        ["type"] = "catalog", ["version"] = 2, ["revision"] = revision,
        ["connections"] = new JsonArray(connections.Select(item => (JsonNode)item).ToArray())
    };

    [TestMethod]
    [DataRow("308046B0AF4A39CB", true)]
    [DataRow("308046B0AF4A39CZ", false)]
    [DataRow("Firefox", false)]
    public void FirefoxCatalogAcceptsOnlyAnExactWindowsIdentity(string applicationId, bool accepted)
    {
        var connection = Connection("firefox", "profile");
        connection["browser"] = "Firefox";
        connection["applicationId"] = applicationId;
        using var json = JsonDocument.Parse(Catalog(1, connection).ToJsonString());
        Assert.AreEqual(accepted ? applicationId : string.Empty, BrowserCatalog.Parse(json.RootElement).Health.Single().ApplicationId);
    }

    private static JsonObject Connection(string id, string profile, long generation = 1, string player = "doc:player") => new()
    {
        ["connectionId"] = id, ["profileId"] = profile, ["browser"] = "Chromium",
        ["applicationId"] = "MSEdge", ["heartbeatAgeMs"] = 0, ["hasSnapshot"] = true,
        ["tabs"] = new JsonArray(new JsonObject { ["tabId"] = 7, ["sessionId"] = player, ["generation"] = generation }),
        ["sessions"] = new JsonArray(new JsonObject
        {
            ["id"] = player, ["tabId"] = 7, ["title"] = "Track", ["state"] = "playing",
            ["duration"] = 120, ["position"] = 15,
            ["capabilities"] = new JsonArray("play", "pause", "activate")
        })
    };

    private sealed class CompanionServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();
        private readonly ConcurrentBag<Task> _clients = [];
        private readonly TaskCompletionSource<NamedPipeServerStream> _watch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _listener;
        private bool _disposed;

        internal CompanionServer(string? pipeName = null)
        {
            this.PipeName = pipeName ?? "BrowserBackendTest." + Guid.NewGuid().ToString("N");
            this._listener = this.ListenAsync();
        }

        internal string PipeName { get; }
        internal string? CommandResult { get; init; } = "completed";
        internal Channel<JsonElement> Commands { get; } = Channel.CreateUnbounded<JsonElement>();

        internal async Task PublishAsync(JsonObject catalog)
        {
            var pipe = await this._watch.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await SendAsync(pipe, catalog, this._lifetime.Token);
        }

        private async Task ListenAsync()
        {
            try
            {
                while (true)
                {
                    var pipe = new NamedPipeServerStream(this.PipeName, PipeDirection.InOut, 16, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    try { await pipe.WaitForConnectionAsync(this._lifetime.Token); }
                    catch { pipe.Dispose(); throw; }
                    this._clients.Add(this.HandleAsync(pipe));
                }
            }
            catch (OperationCanceledException) when (this._lifetime.IsCancellationRequested) { }
        }

        private async Task HandleAsync(NamedPipeServerStream pipe)
        {
            await using var owned = pipe;
            try
            {
                using var request = await BrowserWire.ReadAsync(pipe, this._lifetime.Token);
                if (request.RootElement.GetProperty("type").GetString() == "watch")
                {
                    await SendAsync(pipe, Catalog(0), this._lifetime.Token);
                    this._watch.TrySetResult(pipe);
                    await Task.Delay(Timeout.Infinite, this._lifetime.Token);
                }
                else
                {
                    this.Commands.Writer.TryWrite(request.RootElement.Clone());
                    if (this.CommandResult is null) await Task.Delay(Timeout.Infinite, this._lifetime.Token);
                    else await SendAsync(pipe, new JsonObject { ["status"] = this.CommandResult }, this._lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (this._lifetime.IsCancellationRequested) { }
        }

        private static Task SendAsync(Stream stream, JsonObject message, CancellationToken cancellationToken) =>
            BrowserWire.WriteAsync(stream, writer =>
            {
                foreach (var property in message)
                {
                    writer.WritePropertyName(property.Key);
                    property.Value!.WriteTo(writer);
                }
            }, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (this._disposed) return;
            this._disposed = true;
            await this._lifetime.CancelAsync();
            await this._listener;
            await Task.WhenAll(this._clients);
            this._lifetime.Dispose();
        }
    }
}