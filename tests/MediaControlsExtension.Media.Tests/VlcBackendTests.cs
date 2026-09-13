// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.Vlc;
using VlcStrings = JPSoftworks.MediaControlsExtension.Media.Vlc.Resources.Strings;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class VlcBackendTests
{
    private static readonly byte[] ArtworkPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg==");

    [TestMethod]
    public async Task ForbiddenRemoteServerReportsAccessRulesWithoutExposingCredentials()
    {
        var server = new VlcServer { StatusCode = HttpStatusCode.Forbidden };
        await using var backend = CreateBackend(server,
            () => new(Password: "test-secret") { Endpoint = "http://remote-pc:8080" });
        await backend.StartAsync(default);
        var snapshot = await backend.ReadSnapshotAsync(default);
        Assert.AreEqual(MediaConnectionStatus.Disconnected, snapshot.Connection.Status);
        Assert.AreEqual(VlcStrings.Connection_AccessDenied, snapshot.Connection.DiagnosticMessage);
        Assert.DoesNotContain("test-secret", snapshot.Connection.DiagnosticMessage!);
        Assert.IsEmpty(snapshot.Sessions);
        Assert.IsEmpty(server.Commands);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RemoteTransportAndArtworkUseTheConfiguredEndpointWithoutNativeIdentity(bool treatAsLocal)
    {
        var server = new VlcServer { ArtworkUrl = "file:///remote/cover.png" };
        var options = new VlcConnectionOptions(Password: "remote-secret")
        {
            Endpoint = "https://living-room:8443", TreatAsLocal = treatAsLocal,
        };
        await using var backend = new VlcBackend(() => options, server.CreateHandler,
            TimeSpan.FromDays(1), TimeSpan.FromSeconds(5), sourceIconPath: "provider.svg");
        await backend.StartAsync(default);
        var snapshot = await backend.ReadSnapshotAsync(default);
        var session = snapshot.Sessions.Single();
        Assert.AreEqual(session.Origin.ConnectionId, snapshot.Connection.Connections.Single().Id);
        Assert.IsNull(session.MediaProperties.Source.NativeApplication);
        Assert.AreEqual("provider.svg", session.MediaProperties.Source.IconPath);
        Assert.AreEqual("https://living-room:8443/", session.Origin.ConnectionId);
        Assert.AreEqual(treatAsLocal, session.Origin.TreatAsLocal);
        Assert.AreEqual(MediaBackendCommandStatus.Completed,
            (await backend.ExecuteAsync(Command(session, MediaOperation.Pause), default)).Status);
        Assert.IsNotNull(await backend.GetArtworkAsync(session.MediaProperties.Artwork!.Value, default));
        Assert.IsTrue(server.Requests.All(request => request.Uri.GetLeftPart(UriPartial.Authority) == "https://living-room:8443"));
        Assert.IsTrue(server.Requests.All(request => request.Authorization ==
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":remote-secret"))));
    }

    [TestMethod]
    public async Task ChangingEndpointDuringObservationCannotSendAnOldCommandToTheNewServer()
    {
        var server = new VlcServer();
        var options = new VlcConnectionOptions(Password: "first-secret") { Endpoint = "http://first-pc:8080" };
        await using var backend = CreateBackend(server, () => options);
        await backend.StartAsync(default);
        var original = (await backend.ReadSnapshotAsync(default)).Sessions.Single();
        var entered = NewSignal();
        var release = NewSignal();
        server.BeforeResponse = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var command = backend.ExecuteAsync(Command(original, MediaOperation.SkipNext), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        options = options with { Endpoint = "http://second-pc:8080", Password = "second-secret" };
        release.TrySetResult();
        Assert.AreEqual(MediaBackendCommandStatus.SessionGone, (await command).Status);
        Assert.IsEmpty(server.Commands);
        var replacement = (await backend.ReadSnapshotAsync(default)).Sessions.Single();
        Assert.AreNotEqual(original.BindingGeneration, replacement.BindingGeneration);
        Assert.AreNotEqual(original.Origin.ConnectionId, replacement.Origin.ConnectionId);
        Assert.AreEqual(MediaBackendCommandStatus.Completed,
            (await backend.ExecuteAsync(Command(replacement, MediaOperation.SkipNext), default)).Status);
        Assert.AreEqual("second-pc", server.Requests.Last().Uri.Host);
    }

    [TestMethod]
    public async Task ArtworkInFlightCannotSurviveAnEndpointChange()
    {
        var server = new VlcServer { ArtworkUrl = "file:///cover.png" };
        var options = new VlcConnectionOptions(Password: "first-secret") { Endpoint = "http://first-pc:8080" };
        await using var backend = CreateBackend(server, () => options);
        await backend.StartAsync(default);
        var session = (await backend.ReadSnapshotAsync(default)).Sessions.Single();
        var entered = NewSignal();
        var release = NewSignal();
        server.BeforeResponse = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/art")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };
        var artwork = backend.GetArtworkAsync(session.MediaProperties.Artwork!.Value, default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        options = options with { Endpoint = "http://second-pc:8080", Password = "second-secret" };
        release.TrySetResult();
        Assert.IsNull(await artwork);
        var replacement = (await backend.ReadSnapshotAsync(default)).Sessions.Single();
        Assert.AreNotEqual(session.MediaProperties.Artwork, replacement.MediaProperties.Artwork);
        Assert.AreEqual("first-pc", server.ArtworkRequests.Single().Uri.Host);
    }

    [TestMethod]
    public async Task TreatmentChangesFenceOldCommandsButPreserveLocalNativeIdentity()
    {
        var server = new VlcServer();
        var options = new VlcConnectionOptions(8080, "test-secret");
        await using var backend = CreateBackend(server, () => options);
        await backend.StartAsync(default);
        var local = (await backend.ReadSnapshotAsync(default)).Sessions.Single();
        Assert.IsTrue(local.Origin.TreatAsLocal);
        options = options with { TreatAsLocal = false };
        var remote = (await backend.ReadSnapshotAsync(default)).Sessions.Single();
        Assert.IsFalse(remote.Origin.TreatAsLocal);
        Assert.AreEqual(local.Origin.ConnectionId, remote.Origin.ConnectionId);
        Assert.AreEqual(new("vlc.exe"), remote.MediaProperties.Source.NativeApplication);
        Assert.AreNotEqual(local.BindingGeneration, remote.BindingGeneration);
        Assert.AreEqual(MediaBackendCommandStatus.SessionGone,
            (await backend.ExecuteAsync(Command(local, MediaOperation.Play), default)).Status);
        Assert.IsEmpty(server.Commands);
    }

    [TestMethod]
    public async Task ShuffleAndRepeatUseFreshStateAndTheExistingRepeatCycle()
    {
        var server = new VlcServer();
        await using var backend = CreateBackend(server);
        await backend.StartAsync(default);
        var session = (await backend.ReadSnapshotAsync(default)).Sessions.Single();
        Assert.AreNotEqual(MediaCapabilities.None, session.Capabilities & MediaCapabilities.ToggleRepeat);
        Assert.AreNotEqual(MediaCapabilities.None, session.Capabilities & MediaCapabilities.ToggleShuffle);
        await backend.ExecuteAsync(Command(session, MediaOperation.ToggleShuffle), default);
        Assert.IsTrue(server.Shuffle);
        await backend.ExecuteAsync(Command(session, MediaOperation.ToggleShuffle), default);
        Assert.IsFalse(server.Shuffle);
        await backend.ExecuteAsync(Command(session, MediaOperation.ToggleRepeat), default);
        Assert.IsTrue(server.Repeat && !server.Loop);
        await backend.ExecuteAsync(Command(session, MediaOperation.ToggleRepeat), default);
        Assert.IsTrue(!server.Repeat && server.Loop);
        await backend.ExecuteAsync(Command(session, MediaOperation.ToggleRepeat), default);
        Assert.IsFalse(server.Repeat || server.Loop);

        server.Repeat = true;
        await backend.ExecuteAsync(Command(session, MediaOperation.ToggleRepeat), default);
        Assert.IsTrue(server.Loop && !server.Repeat, "A repeat command must read changes made outside Media Controls.");
    }

    [TestMethod]
    public async Task MissingModeFlagsDoNotAdvertiseOrSendShuffleAndRepeatCommands()
    {
        var server = new VlcServer { StatusJsonOverride = "{\"apiversion\":3,\"version\":\"3.0.23\",\"state\":\"playing\",\"currentplid\":3}" };
        await using var backend = CreateBackend(server);
        await backend.StartAsync(default);
        var session = (await backend.ReadSnapshotAsync(default)).Sessions.Single();
        Assert.AreEqual(MediaCapabilities.None, session.Capabilities & (MediaCapabilities.ToggleRepeat | MediaCapabilities.ToggleShuffle));
        Assert.AreEqual(MediaBackendCommandStatus.Unsupported,
            (await backend.ExecuteAsync(Command(session, MediaOperation.ToggleShuffle), default)).Status);
        Assert.AreEqual(MediaBackendCommandStatus.Unsupported,
            (await backend.ExecuteAsync(Command(session, MediaOperation.ToggleRepeat), default)).Status);
        Assert.IsEmpty(server.Commands);
    }

    [TestMethod]
    public async Task ArtworkUsesTheLocalItemEndpointAndCachesTheValidatedImage()
    {
        var server = new VlcServer { ArtworkUrl = "https://example.invalid/cover.png" };
        await using var backend = CreateBackend(server);
        await backend.StartAsync(default);
        var first = (await backend.ReadSnapshotAsync(default)).Sessions.Single();
        var key = first.MediaProperties.Artwork!.Value;
        var content = await backend.GetArtworkAsync(key, default);
        Assert.IsNotNull(content);
        Assert.AreEqual("image/png", content.ContentType);
        CollectionAssert.AreEqual(ArtworkPng, content.Data.ToArray());
        Assert.IsFalse(string.IsNullOrEmpty(content.Hash));
        Assert.AreSame(content, await backend.GetArtworkAsync(key, default));
        Assert.AreEqual(key, (await backend.ReadSnapshotAsync(default)).Sessions.Single().MediaProperties.Artwork);
        Assert.AreEqual(1, server.ArtworkRequests.Length);
        Assert.AreEqual("http://127.0.0.1:8080/art?item=3", server.ArtworkRequests.Single().Uri.ToString());
        Assert.AreEqual(server.Requests.First().Authorization, server.ArtworkRequests.Single().Authorization);
        Assert.IsEmpty(server.Commands);
    }

    [TestMethod]
    public async Task SlowArtworkDoesNotBlockControlsAndCannotPublishAfterAnUnobservedTrackChange()
    {
        var server = new VlcServer { ArtworkUrl = "file:///cover.png" };
        var entered = NewSignal();
        var release = NewSignal();
        server.BeforeResponse = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/art")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };
        await using var backend = CreateBackend(server);
        await backend.StartAsync(default);
        var session = (await backend.ReadSnapshotAsync(default)).Sessions.Single();
        var oldKey = session.MediaProperties.Artwork!.Value;
        var artwork = backend.GetArtworkAsync(oldKey, default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var pause = await backend.ExecuteAsync(Command(session, MediaOperation.Pause), default).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(MediaBackendCommandStatus.Completed, pause.Status);
            server.Title = "Another track in the same stream";
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.IsNull(await artwork);
        var replacement = (await backend.ReadSnapshotAsync(default)).Sessions.Single();
        Assert.AreNotEqual(oldKey, replacement.MediaProperties.Artwork);
        Assert.IsNull(await backend.GetArtworkAsync(oldKey, default));
        Assert.AreEqual(1, server.ArtworkRequests.Length);
    }

    [TestMethod]
    public async Task ArtworkKeysAreRetiredAcrossTracksMissingArtworkAndReconnections()
    {
        var server = new VlcServer { ArtworkUrl = "file:///cover.png" };
        await using var backend = CreateBackend(server);
        await backend.StartAsync(default);
        var first = (await backend.ReadSnapshotAsync(default)).Sessions.Single();
        var original = first.MediaProperties.Artwork!.Value;
        server.ItemId++;
        var changed = (await backend.ReadSnapshotAsync(default)).Sessions.Single().MediaProperties.Artwork!.Value;
        Assert.AreNotEqual(original, changed);
        server.ArtworkUrl = null;
        Assert.IsNull((await backend.ReadSnapshotAsync(default)).Sessions.Single().MediaProperties.Artwork);
        Assert.IsNull(await backend.GetArtworkAsync(changed, default));
        server.ArtworkUrl = "file:///cover.png";
        server.ItemId--;
        var restored = (await backend.ReadSnapshotAsync(default)).Sessions.Single().MediaProperties.Artwork!.Value;
        Assert.AreNotEqual(original, restored);
        server.StatusCode = HttpStatusCode.Unauthorized;
        Assert.IsEmpty((await backend.ReadSnapshotAsync(default)).Sessions);
        server.StatusCode = HttpStatusCode.OK;
        var reconnected = (await backend.ReadSnapshotAsync(default)).Sessions.Single();
        Assert.AreNotEqual(first.BindingGeneration, reconnected.BindingGeneration);
        Assert.AreNotEqual(restored, reconnected.MediaProperties.Artwork);
        Assert.IsNull(await backend.GetArtworkAsync(restored, default));
        Assert.IsEmpty(server.ArtworkRequests);
    }

    [TestMethod]
    public async Task ChangedConnectionDiscardsArtworkAlreadyInFlight()
    {
        var options = new VlcConnectionOptions(8080, "first-secret");
        var server = new VlcServer { ArtworkUrl = "file:///cover.png" };
        var entered = NewSignal();
        var release = NewSignal();
        server.BeforeResponse = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/art")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };
        await using var backend = CreateBackend(server, () => options);
        await backend.StartAsync(default);
        var key = (await backend.ReadSnapshotAsync(default)).Sessions.Single().MediaProperties.Artwork!.Value;
        var load = backend.GetArtworkAsync(key, default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        options = new(8090, "replacement-secret");
        release.SetResult();
        Assert.IsNull(await load);
        var fresh = (await backend.ReadSnapshotAsync(default)).Sessions.Single();
        Assert.AreNotEqual(key, fresh.MediaProperties.Artwork);
        Assert.AreEqual(8080, server.ArtworkRequests.Single().Uri.Port);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("invalid")]
    [DataRow("oversized")]
    [DataRow("streaming-oversized")]
    [DataRow("timeout")]
    public async Task ArtworkFailuresKeepThePlayerAvailableAndRetryWithANewKey(string failure)
    {
        var clock = new TestTimeProvider();
        var server = new VlcServer { ArtworkUrl = "file:///cover.png" };
        server.ArtworkStatusCode = failure == "missing" ? HttpStatusCode.NotFound : HttpStatusCode.OK;
        server.CreateArtworkContent = failure switch
        {
            "invalid" => () => new StringContent("<html>Not an image</html>"),
            "oversized" => () =>
            {
                var content = new ByteArrayContent(ArtworkPng);
                content.Headers.ContentLength = (32 * 1024 * 1024) + 1;
                return content;
            },
            "streaming-oversized" => () => new StreamingArtworkContent(block: false),
            "timeout" => () => new StreamingArtworkContent(block: true),
            _ => () => new ByteArrayContent(ArtworkPng),
        };
        await using var backend = CreateBackend(server, requestTimeout: TimeSpan.FromMilliseconds(200), timeProvider: clock);
        await backend.StartAsync(default);
        var first = (await backend.ReadSnapshotAsync(default)).Sessions.Single();
        var key = first.MediaProperties.Artwork!.Value;
        Assert.IsNull(await backend.GetArtworkAsync(key, default));
        Assert.IsNull(await backend.GetArtworkAsync(key, default));
        var snapshot = await backend.ReadSnapshotAsync(default);
        Assert.AreEqual(MediaConnectionStatus.Connected, snapshot.Connection.Status);
        Assert.AreEqual(first.BindingGeneration, snapshot.Sessions.Single().BindingGeneration);
        Assert.AreEqual(key, snapshot.Sessions.Single().MediaProperties.Artwork);
        Assert.AreEqual(1, server.ArtworkRequests.Length);
        clock.Advance(TimeSpan.FromSeconds(11));
        server.ArtworkStatusCode = HttpStatusCode.OK;
        server.CreateArtworkContent = () => new ByteArrayContent(ArtworkPng);
        var retryKey = (await backend.ReadSnapshotAsync(default)).Sessions.Single().MediaProperties.Artwork!.Value;
        Assert.AreNotEqual(key, retryKey);
        Assert.IsNull(await backend.GetArtworkAsync(key, default));
        Assert.IsNotNull(await backend.GetArtworkAsync(retryKey, default));
        Assert.AreEqual(2, server.ArtworkRequests.Length);
    }

    [TestMethod]
    public async Task DisposalCancelsAndDrainsArtworkAlongsideTheControlClient()
    {
        var server = new VlcServer { ArtworkUrl = "file:///cover.png" };
        var entered = NewSignal();
        var canceled = NewSignal();
        var release = NewSignal();
        server.BeforeResponse = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/art")
            {
                using var registration = token.Register(() => canceled.TrySetResult());
                entered.TrySetResult();
                await release.Task;
            }
        };
        var backend = CreateBackend(server);
        await backend.StartAsync(default);
        var key = (await backend.ReadSnapshotAsync(default)).Sessions.Single().MediaProperties.Artwork!.Value;
        var load = backend.GetArtworkAsync(key, default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = backend.DisposeAsync().AsTask();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(disposal.IsCompleted);
        release.TrySetResult();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await load);
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, server.DisposedHandlers);
    }

    [TestMethod]
    public async Task AuthenticatedStatusPublishesMetadataAndOnePlayer()
    {
        var server = new VlcServer();
        await using var backend = CreateBackend(server);
        await backend.StartAsync(default);
        var snapshot = await backend.ReadSnapshotAsync(default);
        Assert.AreEqual(MediaConnectionStatus.Connected, snapshot.Connection.Status);
        var session = snapshot.Sessions.Single();
        Assert.AreEqual("VLC", session.MediaProperties.Source.DisplayName);
        Assert.AreEqual("First song", session.MediaProperties.Title);
        Assert.AreEqual("Artist", session.MediaProperties.Artist);
        Assert.AreEqual("Album", session.MediaProperties.AlbumTitle);
        Assert.AreEqual(2, session.MediaProperties.TrackNumber);
        Assert.AreEqual(TimeSpan.FromSeconds(15), session.TimelineProperties.Position);
        Assert.AreEqual(TimeSpan.FromSeconds(180), session.TimelineProperties.Duration);
        Assert.AreEqual(MediaPlaybackState.Playing, session.PlaybackState);
        Assert.AreEqual(MediaCapabilities.None, session.Capabilities & MediaCapabilities.ActivateSource);
        var request = server.Requests.Single();
        Assert.AreEqual("127.0.0.1", request.Uri.Host);
        Assert.AreEqual("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":test-secret")), request.Authorization);
        Assert.DoesNotContain("test-secret", request.Uri.ToString());
        Assert.DoesNotContain("test-secret", new VlcConnectionOptions(8080, "test-secret").ToString());
    }

    [TestMethod]
    public async Task EmptyPlayerIsConnectedWithoutSessionAndQueuedMediaCanBePlayed()
    {
        var server = new VlcServer { State = "stopped", HasPlaylist = false };
        await using var backend = CreateBackend(server);
        await backend.StartAsync(default);
        var empty = await backend.ReadSnapshotAsync(default);
        Assert.AreEqual(MediaConnectionStatus.Connected, empty.Connection.Status);
        Assert.IsEmpty(empty.Sessions);
        server.HasPlaylist = true;
        var stopped = await backend.ReadSnapshotAsync(default);
        var session = stopped.Sessions.Single();
        Assert.AreEqual("Queued song", session.MediaProperties.Title);
        Assert.AreEqual(MediaPlaybackState.Stopped, session.PlaybackState);
        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await backend.ExecuteAsync(Command(session, MediaOperation.Play), default)).Status);
        Assert.AreEqual("?command=pl_play", server.Commands.Single());
    }

    [TestMethod]
    public async Task PauseIsIdempotentAndResumeDoesNotRestartTheTrack()
    {
        var server = new VlcServer();
        await using var backend = CreateBackend(server);
        await backend.StartAsync(default);
        var session = (await backend.ReadSnapshotAsync(default)).Sessions.Single();
        foreach (var operation in new[] { MediaOperation.Pause, MediaOperation.Pause, MediaOperation.Play })
        {
            Assert.AreEqual(MediaBackendCommandStatus.Completed, (await backend.ExecuteAsync(Command(session, operation), default)).Status);
        }

        string[] expected = ["?command=pl_forcepause", "?command=pl_forcepause", "?command=pl_forceresume"];
        CollectionAssert.AreEqual(expected, server.Commands);
        Assert.AreEqual("playing", server.State);
    }

    [TestMethod]
    public async Task TransportOperationsAreExplicitAndUnsupportedCommandsNeverSendControls()
    {
        var server = new VlcServer();
        await using var backend = CreateBackend(server);
        await backend.StartAsync(default);
        var session = (await backend.ReadSnapshotAsync(default)).Sessions.Single();
        foreach (var operation in new[] { MediaOperation.SkipNext, MediaOperation.SkipPrevious, MediaOperation.Stop })
        {
            Assert.AreEqual(MediaBackendCommandStatus.Completed, (await backend.ExecuteAsync(Command(session, operation), default)).Status);
        }

        Assert.AreEqual(MediaBackendCommandStatus.Unsupported,
            (await backend.ExecuteAsync(Command(session, MediaOperation.ActivateSource), default)).Status);
        string[] expected = ["?command=pl_next", "?command=pl_previous", "?command=pl_stop"];
        CollectionAssert.AreEqual(expected, server.Commands);
    }

    [TestMethod]
    public async Task AuthenticationFailureWithdrawsSessionAndReconnectFencesOldCommands()
    {
        var server = new VlcServer();
        await using var backend = CreateBackend(server);
        await backend.StartAsync(default);
        var first = await backend.ReadSnapshotAsync(default);
        var old = first.Sessions.Single();
        server.StatusCode = HttpStatusCode.Unauthorized;
        var disconnected = await backend.ReadSnapshotAsync(default);
        Assert.IsEmpty(disconnected.Sessions);
        Assert.AreEqual(VlcStrings.Connection_PasswordRejected, disconnected.Connection.DiagnosticMessage);
        server.StatusCode = HttpStatusCode.OK;
        var reconnected = await backend.ReadSnapshotAsync(default);
        Assert.IsTrue(reconnected.Revision > disconnected.Revision && disconnected.Revision > first.Revision);
        Assert.AreNotEqual(old.BindingGeneration, reconnected.Sessions.Single().BindingGeneration);
        Assert.IsNull(reconnected.Connection.DiagnosticMessage);
        Assert.AreEqual(MediaBackendCommandStatus.SessionGone,
            (await backend.ExecuteAsync(Command(old, MediaOperation.Pause), default)).Status);
        Assert.IsEmpty(server.Commands);
    }

    [TestMethod]
    public async Task PauseFailureDuringFreshObservationIsReportedAsUnavailable()
    {
        var server = new VlcServer();
        await using var backend = CreateBackend(server);
        await backend.StartAsync(default);
        var session = (await backend.ReadSnapshotAsync(default)).Sessions.Single();
        server.StatusCode = HttpStatusCode.ServiceUnavailable;
        var result = await backend.ExecuteAsync(Command(session, MediaOperation.Pause), default);
        Assert.AreEqual(MediaBackendCommandStatus.Unavailable, result.Status);
        Assert.IsEmpty(server.Commands);
    }

    [TestMethod]
    public async Task ConnectionSettingsChangedDuringObservationDiscardItsResult()
    {
        var server = new VlcServer();
        var options = new VlcConnectionOptions(8080, "first-secret");
        await using var backend = CreateBackend(server, () => options);
        await backend.StartAsync(default);
        var old = (await backend.ReadSnapshotAsync(default)).Sessions.Single();
        var entered = NewSignal();
        var release = NewSignal();
        server.BeforeResponse = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var observation = backend.ReadSnapshotAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        options = new(8090, "second-secret");
        var command = backend.ExecuteAsync(Command(old, MediaOperation.Play), default);
        release.SetResult();
        Assert.IsEmpty((await observation).Sessions);
        Assert.AreEqual(MediaBackendCommandStatus.SessionGone, (await command).Status);
        server.BeforeResponse = null;
        var replacement = (await backend.ReadSnapshotAsync(default)).Sessions.Single();
        Assert.AreNotEqual(old.BindingGeneration, replacement.BindingGeneration);
        Assert.AreEqual(8090, server.Requests.Last().Uri.Port);
        Assert.AreEqual(1, server.DisposedHandlers);
        Assert.IsEmpty(server.Commands);
    }

    [TestMethod]
    public async Task InvalidConfigurationDoesNotCreateAClient()
    {
        var server = new VlcServer();
        var options = new VlcConnectionOptions(0, "secret");
        await using var backend = CreateBackend(server, () => options);
        await backend.StartAsync(default);
        Assert.AreEqual(VlcStrings.Connection_InvalidEndpoint, (await backend.ReadSnapshotAsync(default)).Connection.DiagnosticMessage);
        options = new(8080, "");
        Assert.AreEqual(VlcStrings.Connection_PasswordRequired, (await backend.ReadSnapshotAsync(default)).Connection.DiagnosticMessage);
        Assert.AreEqual(0, server.CreatedHandlers);
        Assert.IsEmpty(server.Requests);
    }

    [TestMethod]
    public async Task InvalidJsonIsARecoverableConnectionFailure()
    {
        var server = new VlcServer { StatusJsonOverride = "invalid" };
        await using var backend = CreateBackend(server);
        await backend.StartAsync(default);
        var invalid = await backend.ReadSnapshotAsync(default);
        Assert.AreEqual(MediaConnectionStatus.Disconnected, invalid.Connection.Status);
        Assert.AreEqual(VlcStrings.Connection_InvalidJson, invalid.Connection.DiagnosticMessage);
        server.StatusJsonOverride = "{\"apiversion\":999}";
        Assert.AreEqual(VlcStrings.Protocol_UnsupportedVersion, (await backend.ReadSnapshotAsync(default)).Connection.DiagnosticMessage);
        server.StatusJsonOverride = null;
        Assert.AreEqual(1, (await backend.ReadSnapshotAsync(default)).Sessions.Length);
    }

    [TestMethod]
    public async Task CommandTimeoutDoesNotReplayAndRetiresTheBinding()
    {
        var server = new VlcServer();
        await using var backend = CreateBackend(server, requestTimeout: TimeSpan.FromMilliseconds(100));
        await backend.StartAsync(default);
        var old = (await backend.ReadSnapshotAsync(default)).Sessions.Single();
        server.BeforeResponse = async (request, token) =>
        {
            if (request.RequestUri!.Query.Length > 0)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        Assert.AreEqual(MediaBackendCommandStatus.Unavailable,
            (await backend.ExecuteAsync(Command(old, MediaOperation.SkipNext), default)).Status);
        server.BeforeResponse = null;
        var fresh = (await backend.ReadSnapshotAsync(default)).Sessions.Single();
        Assert.AreNotEqual(old.BindingGeneration, fresh.BindingGeneration);
        Assert.AreEqual(1, server.Commands.Length);
    }

    [TestMethod]
    public async Task DisposalCancelsAndDrainsAnOutstandingRequest()
    {
        var server = new VlcServer();
        var entered = NewSignal();
        var canceled = NewSignal();
        var release = NewSignal();
        server.BeforeResponse = async (_, token) =>
        {
            using var registration = token.Register(() => canceled.TrySetResult());
            entered.TrySetResult();
            await release.Task;
        };
        var backend = CreateBackend(server);
        await backend.StartAsync(default);
        var read = backend.ReadSnapshotAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = backend.DisposeAsync().AsTask();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(disposal.IsCompleted);
        Assert.AreEqual(0, server.DisposedHandlers);
        release.SetResult();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await read);
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        await backend.DisposeAsync();
        Assert.AreEqual(1, server.DisposedHandlers);
    }

    [TestMethod]
    public async Task CompositeMixesVlcWithIndependentProviderAndReenableCreatesFreshIdentity()
    {
        var server = new VlcServer();
        var other = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Other player"));
        var registry = new MediaBackendRegistry()
            .Register(new("other", "Other provider", "", _ => other, true))
            .Register(new("vlc", "VLC", "", _ => CreateBackend(server), true));
        await using var composite = new CompositeMediaBackend(registry);
        await composite.StartAsync(default);
        var snapshot = await WaitForSessionsAsync(composite, 2);
        var vlc = snapshot.Sessions.Single(session => session.MediaProperties.Source.Provider!.Id == "vlc");
        var secondary = snapshot.Sessions.Single(session => session.MediaProperties.Source.Provider!.Id == "other");
        var result = await composite.ExecuteAsync(new(secondary.Id, secondary.BindingGeneration, MediaOperation.Play,
            [new(vlc.Id, vlc.BindingGeneration)]), default);
        Assert.AreEqual(MediaBackendCommandStatus.Completed, result.Status);
        Assert.AreEqual(MediaBackendCommandStatus.Completed, result.PauseResults.Single().Status);
        Assert.AreEqual("paused", server.State);
        Assert.AreEqual(MediaOperation.Play, other.Commands.Single().Operation);
        await composite.SetEnabledAsync("vlc", false);
        Assert.AreEqual(1, (await WaitForSessionsAsync(composite, 1)).Sessions.Length);
        Assert.AreEqual(1, server.DisposedHandlers);
        await composite.SetEnabledAsync("vlc", true);
        var replacement = (await WaitForSessionsAsync(composite, 2)).Sessions
            .Single(session => session.MediaProperties.Source.Provider!.Id == "vlc");
        Assert.AreNotEqual(vlc.Id, replacement.Id);
        var resume = await composite.ExecuteAsync(new(replacement.Id, replacement.BindingGeneration, MediaOperation.Play,
            [new(secondary.Id, secondary.BindingGeneration)]), default);
        Assert.AreEqual(MediaBackendCommandStatus.Completed, resume.Status);
        Assert.AreEqual(MediaOperation.Pause, other.Commands.Last().Operation);
        Assert.AreEqual("playing", server.State);
    }

    [TestMethod]
    public async Task DroppedCommandResponseDoesNotReplayTheHttpGet()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var commands = 0;
        var server = ServeAsync();
        try
        {
            await using var backend = new VlcBackend(() => new(port, "test-secret"));
            await backend.StartAsync(lifetime.Token);
            var session = (await backend.ReadSnapshotAsync(lifetime.Token)).Sessions.Single();
            var result = await backend.ExecuteAsync(Command(session, MediaOperation.SkipNext), lifetime.Token);
            Assert.AreEqual(MediaBackendCommandStatus.Unavailable, result.Status);
            Assert.AreEqual(1, Volatile.Read(ref commands), "An HTTP retry must not execute a media command twice.");
        }
        finally
        {
            await lifetime.CancelAsync();
            await server;
        }

        async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    while (await reader.ReadLineAsync(lifetime.Token) is { } request)
                    {
                        while (await reader.ReadLineAsync(lifetime.Token) is { Length: > 0 })
                        {
                        }

                        if (request.Contains("?command=", StringComparison.Ordinal))
                        {
                            Interlocked.Increment(ref commands);
                            break;
                        }

                        const string body = "{\"apiversion\":3,\"version\":\"3.0.23\",\"state\":\"playing\",\"currentplid\":3}";
                        var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n{body}");
                        await stream.WriteAsync(response, lifetime.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
    }

    private static VlcBackend CreateBackend(VlcServer server, Func<VlcConnectionOptions>? options = null,
        TimeSpan? requestTimeout = null, TimeProvider? timeProvider = null) =>
        new(options ?? (() => new(8080, "test-secret")), server.CreateHandler, TimeSpan.FromDays(1),
            requestTimeout ?? TimeSpan.FromSeconds(5), timeProvider);

    private static MediaBackendCommand Command(MediaBackendSessionSnapshot session, MediaOperation operation) =>
        new(session.Id, session.BindingGeneration, operation, []);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<MediaBackendSnapshot> WaitForSessionsAsync(CompositeMediaBackend backend, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var snapshot = await backend.ReadSnapshotAsync(timeout.Token);
            if (snapshot.Sessions.Length == count)
            {
                return snapshot;
            }

            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class VlcServer
    {
        public string State { get; set; } = "playing";
        public string Title { get; set; } = "First song";
        public int ItemId { get; set; } = 3;
        public bool Shuffle { get; set; }
        public bool Repeat { get; set; }
        public bool Loop { get; set; }
        public string? ArtworkUrl { get; set; }
        public HttpStatusCode ArtworkStatusCode { get; set; } = HttpStatusCode.OK;
        public Func<HttpContent> CreateArtworkContent { get; set; } = () => new ByteArrayContent(ArtworkPng);
        public bool HasPlaylist { get; set; } = true;
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public string? StatusJsonOverride { get; set; }
        public Func<HttpRequestMessage, CancellationToken, Task>? BeforeResponse { get; set; }
        public ConcurrentQueue<(Uri Uri, string? Authorization)> Requests { get; } = new();
        public string[] Commands => this.Requests.Select(request => request.Uri.Query)
            .Where(query => query.StartsWith("?command=", StringComparison.Ordinal)).ToArray();
        public (Uri Uri, string? Authorization)[] ArtworkRequests => this.Requests.Where(request => request.Uri.AbsolutePath == "/art").ToArray();
        public int CreatedHandlers;
        public int DisposedHandlers;

        public HttpMessageHandler CreateHandler()
        {
            Interlocked.Increment(ref this.CreatedHandlers);
            return new Handler(this);
        }

        private sealed class Handler(VlcServer server) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                server.Requests.Enqueue((request.RequestUri!, request.Headers.Authorization?.ToString()));
                if (server.BeforeResponse is { } before)
                {
                    await before(request, cancellationToken);
                }

                if (server.StatusCode != HttpStatusCode.OK)
                {
                    return new(server.StatusCode);
                }

                if (request.RequestUri!.AbsolutePath == "/art")
                {
                    return new(server.ArtworkStatusCode) { Content = server.CreateArtworkContent() };
                }

                switch (request.RequestUri.Query)
                {
                    case "?command=pl_random":
                        server.Shuffle = !server.Shuffle;
                        break;
                    case "?command=pl_repeat":
                        server.Repeat = !server.Repeat;
                        if (server.Repeat) server.Loop = false;
                        break;
                    case "?command=pl_loop":
                        server.Loop = !server.Loop;
                        if (server.Loop) server.Repeat = false;
                        break;
                }

                server.State = request.RequestUri!.Query switch
                {
                    "?command=pl_forcepause" when server.State == "playing" => "paused",
                    "?command=pl_forceresume" when server.State == "paused" => "playing",
                    "?command=pl_play" => "playing",
                    "?command=pl_stop" => "stopped",
                    _ => server.State,
                };
                var json = request.RequestUri.AbsolutePath.EndsWith("playlist.json", StringComparison.Ordinal)
                    ? (server.HasPlaylist
                        ? "{\"children\":[{\"type\":\"leaf\",\"id\":\"3\",\"name\":\"Queued song\",\"duration\":180}]}"
                        : "{\"children\":[]}")
                    : server.StatusJsonOverride ?? """
                        {"apiversion":3,"version":"3.0.23 Vetinari","state":"$STATE",
                         "currentplid":$ID,"time":15,"length":180,"random":$SHUFFLE,"repeat":$REPEAT,"loop":$LOOP,
                         "information":{"category":{"meta":{"title":$TITLE,"artist":"Artist","album":"Album","track_number":"2/12","artwork_url":$ART}}}}
                        """.Replace("$STATE", server.State, StringComparison.Ordinal)
                        .Replace("$ID", server.State == "stopped" ? "-1" : server.ItemId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                        .Replace("$SHUFFLE", server.Shuffle ? "true" : "false", StringComparison.Ordinal)
                        .Replace("$REPEAT", server.Repeat ? "true" : "false", StringComparison.Ordinal)
                        .Replace("$LOOP", server.Loop ? "true" : "false", StringComparison.Ordinal)
                        .Replace("$TITLE", JsonSerializer.Serialize(server.Title), StringComparison.Ordinal)
                        .Replace("$ART", JsonSerializer.Serialize(server.ArtworkUrl), StringComparison.Ordinal);
                return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    Interlocked.Increment(ref server.DisposedHandlers);
                }

                base.Dispose(disposing);
            }
        }
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => this._now;
        public void Advance(TimeSpan duration) => this._now += duration;
    }

    private sealed class StreamingArtworkContent(bool block) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            this.SerializeToStreamAsync(stream, context, default);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            if (block)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return;
            }

            var chunk = new byte[1024 * 1024];
            for (var i = 0; i < 33; i++)
            {
                await stream.WriteAsync(chunk, cancellationToken);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}