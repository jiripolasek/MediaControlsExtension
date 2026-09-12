// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Threading.Channels;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class MediaSourceSnapshotTests
{
    [TestMethod]
    public async Task CompositeAssignsRegisteredOwnerWithoutUsingDisplayOrNativeIdentity()
    {
        var source = new MediaSourceSnapshot("Shared name", "shared.png")
        {
            NativeApplication = new("shared.app"),
            Provider = new("claimed-owner", "Claimed owner"),
            Details = [new("Profile", "Personal")],
        };
        var first = new FakeMediaBackend(BackendSnapshot(source));
        var second = new FakeMediaBackend(BackendSnapshot(source));
        await using var service = new MediaService(new CompositeMediaBackend(Registry(first, second)));
        await service.StartAsync();
        await WaitUntilAsync(() => service.Sessions.Length == 2);
        var sessions = service.Sessions;
        Assert.AreNotEqual(sessions[0].Id, sessions[1].Id);
        Assert.AreEqual(new("provider0", "Provider 0"), sessions[0].MediaProperties.Source.Provider);
        Assert.AreEqual(new("provider1", "Provider 1"), sessions[1].MediaProperties.Source.Provider);
        Assert.AreEqual(source with { Provider = new("provider1", "Provider 1") }, sessions[1].MediaProperties.Source);
        Assert.AreEqual("claimed-owner", source.Provider.Id);

        var submission = service.TrySubmit(new(MediaCommandTarget.ForSession(sessions[1].Id), MediaOperation.Play));
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, submission.Status);
        Assert.AreEqual(MediaCommandOutcomeStatus.Completed,
            (await submission.Completion!.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.AreEqual(0, first.Commands.Length);
        Assert.AreEqual(new MediaBackendSessionId(1), second.Commands.Single().SessionId);
    }

    [TestMethod]
    public async Task DetailOnlyUpdatesReachExistingSessionWithoutChangingSelection()
    {
        var source = new MediaSourceSnapshot("Site", "site.png") { Details = [new("Profile", "Personal")] };
        var backend = new FakeMediaBackend(BackendSnapshot(source));
        await using var service = new MediaService(new CompositeMediaBackend(Registry(backend)));
        await service.StartAsync();
        await WaitUntilAsync(() => service.Sessions.Length == 1);
        var session = service.Sessions[0];
        var selected = service.TrySubmit(new(MediaCommandTarget.ForSession(session.Id), MediaOperation.Play));
        await selected.Completion!.WaitAsync(TimeSpan.FromSeconds(5));
        var updates = Channel.CreateUnbounded<MediaSessionChangedEventArgs>();
        session.Changed += (_, args) => updates.Writer.TryWrite(args);

        var latest = source with { Details = [new("Profile", "Work"), new("Page", "Playlist")] };
        backend.SetSnapshot(BackendSnapshot(latest) with { Revision = 2 });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var update in updates.Reader.ReadAllAsync(timeout.Token))
        {
            if ((update.Changes & MediaSessionChanges.MediaProperties) != 0)
            {
                Assert.IsFalse(update.Changes.HasFlag(MediaSessionChanges.Rebound));
                break;
            }
        }

        Assert.AreSame(session, service.Sessions[0]);
        Assert.AreSame(session, service.CurrentSession);
        Assert.IsNull(session.MediaProperties.Source.NativeApplication);
        Assert.AreEqual(latest with { Provider = new("provider0", "Provider 0") }, session.MediaProperties.Source);
    }

    [TestMethod]
    public void EquivalentDetailArraysPreservePublishedPropertiesAndRevision()
    {
        var source = new MediaSourceSnapshot("Site", "site.png")
        {
            NativeApplication = new("browser.app", "browser.exe"),
            Provider = new("browser", "Browser"),
            Details = [new("Profile", "Personal"), new("Page", "Playlist")],
        };
        var snapshot = SessionSnapshot(source);
        var session = new MediaSession(snapshot);
        var equivalent = source with { Details = [new("Profile", "Personal"), new("Page", "Playlist")] };
        var changes = session.Apply(snapshot with { MediaProperties = snapshot.MediaProperties with { Source = equivalent } });

        Assert.AreEqual(source, equivalent);
        Assert.AreEqual(source.GetHashCode(), equivalent.GetHashCode());
        Assert.AreEqual(MediaSessionChanges.None, changes);
        Assert.AreEqual(1L, session.Revision);
        Assert.AreSame(snapshot.MediaProperties, session.MediaProperties);
    }

    [TestMethod]
    [DataRow("name")]
    [DataRow("icon")]
    [DataRow("native-identity")]
    [DataRow("provider-id")]
    [DataRow("provider-name")]
    [DataRow("detail-label")]
    [DataRow("detail-value")]
    [DataRow("removed-details")]
    public void SourceChangesArePublishedAsMediaProperties(string field)
    {
        var source = new MediaSourceSnapshot("Site", "site.png")
        {
            Provider = new("browser", "Browser"),
            Details = [new("Profile", "Personal")],
        };
        var changed = field switch
        {
            "name" => source with { DisplayName = "Another site" },
            "icon" => source with { IconPath = "another.png" },
            "native-identity" => source with { NativeApplication = new("browser.app") },
            "provider-id" => source with { Provider = new("another-browser", "Browser") },
            "provider-name" => source with { Provider = new("browser", "Renamed browser") },
            "detail-label" => source with { Details = [new("Account", "Personal")] },
            "detail-value" => source with { Details = [new("Profile", "Work")] },
            _ => source with { Details = [] },
        };
        var snapshot = SessionSnapshot(source);
        var session = new MediaSession(snapshot);

        Assert.AreEqual(MediaSessionChanges.MediaProperties,
            session.Apply(snapshot with { MediaProperties = snapshot.MediaProperties with { Source = changed } }));
        Assert.AreSame(changed, session.MediaProperties.Source);
        Assert.AreEqual(2L, session.Revision);
    }

    [TestMethod]
    public async Task SourcePolicyUsesDeclaredNativeIdentityAndIgnoresPresentationLabels()
    {
        var nativeSnapshot = FakeMediaBackend.CreateSnapshot(1, 1,
            (1, "Remote without identity", MediaPlaybackState.Paused),
            (2, "Mapped native app", MediaPlaybackState.Paused));
        nativeSnapshot = nativeSnapshot with
        {
            Sessions = [.. nativeSnapshot.Sessions.Select(session => session with
            {
                MediaProperties = session.MediaProperties with
                {
                    Source = session.Id.Value == 1
                        ? new("browser.app") { Details = [new("Application", "browser.app")] }
                        : new("Unrelated label") { NativeApplication = new("browser.app") },
                },
            })],
        };
        var native = new FakeSourcePolicyBackend(nativeSnapshot);
        var companion = new FakeMediaBackend(BackendSnapshot(new("Companion")));
        var registry = new MediaBackendRegistry()
            .Register(new("native", "Native", "Test", _ => native, EnabledByDefault: true))
            .Register(new("companion", "Companion", "Test", _ => companion, EnabledByDefault: true)
            {
                ReplacesSources = [new("native", "browser.app")],
            });
        await using var service = new MediaService(new CompositeMediaBackend(registry));
        await service.StartAsync();
        await WaitUntilAsync(() => service.Sessions.Length == 2);

        Assert.IsTrue(service.Sessions.Any(static session => session.MediaProperties.Title == "Remote without identity"));
        Assert.IsFalse(service.Sessions.Any(static session => session.MediaProperties.Title == "Mapped native app"));
        var remote = service.Sessions.Single(static session => session.MediaProperties.Source.Provider!.Id == "native");
        Assert.IsNull(remote.MediaProperties.Source.NativeApplication);
    }

    [TestMethod]
    [DataRow("missing-source")]
    [DataRow("default-details")]
    [DataRow("empty-native-id")]
    [DataRow("empty-detail-label")]
    [DataRow("null-detail-value")]
    public async Task InvalidSourceSnapshotFaultsOnlyItsOwner(string invalid)
    {
        MediaSourceSnapshot source = invalid switch
        {
            "missing-source" => null!,
            "default-details" => new() { Details = default },
            "empty-native-id" => new() { NativeApplication = new(" ") },
            "empty-detail-label" => new() { Details = [new("", "Value")] },
            _ => new() { Details = [new("Label", null!)] },
        };
        var broken = new FakeMediaBackend(BackendSnapshot(source));
        var healthy = new FakeMediaBackend(BackendSnapshot(new("Healthy")));
        await using var service = new MediaService(new CompositeMediaBackend(Registry(broken, healthy)));
        await service.StartAsync();
        await WaitUntilAsync(() => service.Backends.Length == 2 &&
            service.Backends[0].Status == MediaBackendLifecycleStatus.Faulted &&
            service.Backends[1].Status == MediaBackendLifecycleStatus.Ready);

        Assert.AreEqual("The provider returned invalid source presentation.", service.Backends[0].DiagnosticMessage);
        Assert.AreEqual(1, service.Sessions.Length);
        Assert.AreEqual("Healthy", service.Sessions[0].MediaProperties.Source.DisplayName);
    }

    private static MediaBackendRegistry Registry(params FakeMediaBackend[] backends)
    {
        var registry = new MediaBackendRegistry();
        for (var index = 0; index < backends.Length; index++)
        {
            var backend = backends[index];
            registry.Register(new($"provider{index}", $"Provider {index}", "Test", _ => backend, EnabledByDefault: true));
        }

        return registry;
    }

    private static MediaBackendSnapshot BackendSnapshot(MediaSourceSnapshot source)
    {
        var snapshot = FakeMediaBackend.CreateSnapshot(1, "Track");
        return snapshot with
        {
            Sessions = [snapshot.Sessions[0] with
            {
                MediaProperties = snapshot.Sessions[0].MediaProperties with { Source = source },
            }],
        };
    }

    private static MediaSessionSnapshot SessionSnapshot(MediaSourceSnapshot source) => new(
        new(1), 1, true, MediaPropertiesSnapshot.Empty(source), MediaTimelinePropertiesSnapshot.Empty,
        new(MediaPlaybackState.Paused, MediaPlaybackState.Paused, false, MediaCapabilities.Play, MediaOperation.Play));

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}