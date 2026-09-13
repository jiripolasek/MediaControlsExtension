// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Gsmtc;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class MediaSourcePolicyTests
{
    [TestMethod]
    public async Task RapidClaimChangesFenceLateReadsAndSurviveCallerCancellation()
    {
        var native = new FakeSourcePolicyBackend(NativeSnapshot());
        var companion = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Companion"));
        await using var composite = new CompositeMediaBackend(Registry(native, companion, companionEnabled: true));
        await composite.SetSourceClaimsAsync("companion", []);
        await composite.StartAsync(default);
        var initial = await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 4);
        var original = initial.Sessions.Single(static session => session.MediaProperties.Title == "Browser");
        native.BlockNextRead();
        native.Inner.SetSnapshot(NativeSnapshot(2, "Stale"));
        await WaitUntilAsync(() =>
        {
            _ = composite.ReadSnapshotAsync(default);
            return native.ReadCaptured.Task.IsCompleted;
        });
        using var cancellation = new CancellationTokenSource();
        var claiming = composite.SetSourceClaimsAsync("companion", [new("native", "browser.app")], cancellation.Token);
        try
        {
            cancellation.Cancel();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => claiming);
            var releasing = composite.SetSourceClaimsAsync("companion", []);
            native.Inner.SetSnapshot(NativeSnapshot(3, "Fresh"));
            native.ReleaseRead();
            await releasing.WaitAsync(TimeSpan.FromSeconds(5));
            var restored = await WaitForSnapshotAsync(composite,
                static snapshot => snapshot.Sessions.Any(static session => session.MediaProperties.Title == "Fresh"));
            Assert.IsFalse(restored.Sessions.Any(static session => session.MediaProperties.Title == "Stale"));
            Assert.AreNotEqual(original.Id, restored.Sessions.Single(static session => session.MediaProperties.Title == "Fresh").Id);
            Assert.IsEmpty(native.Policy.ExcludedApplicationIds);
            Assert.AreEqual(0, companion.DisposeCount);
        }
        finally
        {
            native.ReleaseRead();
        }
    }

    [TestMethod]
    public async Task ConfiguredClaimsCanChangeWhileEnabledWithoutRecreatingProviders()
    {
        var native = new FakeSourcePolicyBackend(NativeSnapshot());
        var companion = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Companion"));
        await using var composite = new CompositeMediaBackend(Registry(native, companion, companionEnabled: true));
        await composite.StartAsync(default);
        await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 2);
        await composite.SetSourceClaimsAsync("companion", []);
        var remote = await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 4);
        var restored = remote.Sessions.Single(static session => session.MediaProperties.Title == "Browser");
        Assert.IsEmpty(native.Policy.ExcludedApplicationIds);
        Assert.IsTrue(composite.Backends.Single(static backend => backend.Id == "companion").IsEnabled);

        await composite.SetSourceClaimsAsync("companion", [new("native", "browser.app")]);
        var local = await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 2);
        Assert.IsFalse(local.Sessions.Any(session => session.Id == restored.Id));
        Assert.AreEqual(MediaBackendCommandStatus.SessionGone,
            (await composite.ExecuteAsync(new(restored.Id, restored.BindingGeneration, MediaOperation.Play, []), default)).Status);
        Assert.AreEqual(0, companion.DisposeCount);
        Assert.AreEqual(0, native.Inner.DisposeCount);
        companion.SetSnapshot(new(2, [], [], MediaControlAvailability.Unavailable));
        await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 1);
        Assert.IsTrue(native.Policy.ExcludedApplicationIds.Contains("browser.app"));
    }

    [TestMethod]
    public async Task ClaimsChangedWhileDisabledApplyOnEnableAndConflictsAreAtomic()
    {
        var native = new FakeSourcePolicyBackend(NativeSnapshot());
        var companion = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Companion"));
        var second = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Second"));
        var registry = Registry(native, companion).Register(new("second", "Second", "Test", _ => second, true)
        {
            ReplacesSources = [new("native", "browser.app")],
        });
        await using var composite = new CompositeMediaBackend(registry);
        await composite.SetSourceClaimsAsync("companion", []);
        Assert.IsFalse(composite.Backends.Single(static backend => backend.Id == "companion").IsEnabled);
        await composite.StartAsync(default);
        await composite.SetEnabledAsync("companion", true);
        await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 3);
        Assert.ThrowsExactly<InvalidOperationException>(() => composite.SetSourceClaimsAsync("companion", [new("native", "browser.app")]));
        Assert.ThrowsExactly<ArgumentException>(() => composite.SetSourceClaimsAsync("companion", [new("missing", "app")]));
        await composite.SetEnabledAsync("second", false);
        await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 4);
        Assert.IsEmpty(native.Policy.ExcludedApplicationIds);
    }

    [TestMethod]
    public async Task EnableDisconnectDisableTransfersOnlyClaimedSources()
    {
        var native = new FakeSourcePolicyBackend(NativeSnapshot());
        var companion = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Companion"));
        var composite = new CompositeMediaBackend(Registry(native, companion));
        await using var service = new MediaService(composite);
        await service.StartAsync();
        await WaitUntilAsync(() => service.Sessions.Length == 3);
        var original = service.Sessions.Single(static session => session.MediaProperties.Title == "Browser");
        var unrelated = service.Sessions.Single(static session => session.MediaProperties.Title == "Other");

        await composite.SetEnabledAsync("companion", true);
        await WaitUntilAsync(() => service.Sessions.Length == 2);
        Assert.IsTrue(native.Policy.ExcludedApplicationIds.SetEquals(["browser.app"]));
        Assert.IsFalse(original.IsAvailable);
        Assert.AreSame(unrelated, service.Sessions.Single(static session => session.MediaProperties.Title == "Other"));
        Assert.IsFalse(service.Sessions.Any(static session => session.MediaProperties.Source.NativeApplication?.ApplicationId == "browser.app"));
        Assert.AreEqual(0, native.Inner.DisposeCount);

        companion.SetSnapshot(new(2, [], [], MediaControlAvailability.Available)
        {
            Connection = new(MediaConnectionStatus.Disconnected, "Waiting for the browser"),
        });
        await WaitUntilAsync(() => service.Sessions.Length == 1);
        Assert.AreEqual(MediaConnectionStatus.Disconnected, service.Backends.Single(static state => state.Id == "companion").Connection.Status);
        Assert.AreSame(unrelated, service.CurrentSession);
        Assert.IsTrue(native.Policy.ExcludedApplicationIds.Contains("browser.app"));

        await composite.SetEnabledAsync("companion", false);
        await WaitUntilAsync(() => service.Sessions.Length == 3);
        var restored = service.Sessions.Single(static session => session.MediaProperties.Title == "Browser");
        Assert.AreNotEqual(original.Id, restored.Id);
        Assert.AreSame(unrelated, service.Sessions.Single(static session => session.MediaProperties.Title == "Other"));
        Assert.IsEmpty(native.Policy.ExcludedApplicationIds);
        Assert.AreEqual(MediaCommandSubmissionStatus.SessionGone,
            service.TrySubmit(new(MediaCommandTarget.ForSession(original.Id), MediaOperation.Play)).Status);
        Assert.IsNull(await service.GetArtworkAsync(original.MediaProperties.Artwork!.Value));
        var play = service.TrySubmit(new(MediaCommandTarget.ForSession(restored.Id), MediaOperation.Play));
        Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await play.Completion!.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.AreEqual(0, native.Inner.DisposeCount);
    }

    [TestMethod]
    public async Task SavedEnablementAppliesExclusionsBeforeTheReplacedBackendStarts()
    {
        var native = new FakeSourcePolicyBackend(NativeSnapshot());
        var companion = new FakeMediaBackend(new(1, [], [], MediaControlAvailability.Available));
        await using var composite = new CompositeMediaBackend(Registry(native, companion, companionEnabled: true));
        await composite.StartAsync(default);
        var snapshot = await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 1);
        Assert.IsGreaterThan(0L, native.PolicyRevisionAtStart);
        Assert.AreEqual("Other", snapshot.Sessions.Single().MediaProperties.Title);
        Assert.IsEmpty(snapshot.CurrentSessionHints);
    }

    [TestMethod]
    public async Task SlowPolicyWithdrawsRoutesImmediatelyWhileOtherProvidersProgress()
    {
        var native = new FakeSourcePolicyBackend(NativeSnapshot()) { BlockPolicy = true };
        var companion = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Companion"));
        var independent = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Independent"));
        var registry = Registry(native, companion).Register(new("independent", "Independent", "Test provider", _ => independent));
        await using var composite = new CompositeMediaBackend(registry);
        await composite.StartAsync(default);
        var before = await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 3);
        var browser = before.Sessions.Single(static session => session.MediaProperties.Title == "Browser");
        var other = before.Sessions.Single(static session => session.MediaProperties.Title == "Other");
        var enabling = composite.SetEnabledAsync("companion", true);
        try
        {
            await native.PolicyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(enabling.IsCompleted);
            await WaitUntilAsync(() => composite.Backends[1].Status == MediaBackendLifecycleStatus.Ready);
            var repeatedEnable = composite.SetEnabledAsync("companion", true);
            Assert.IsFalse(repeatedEnable.IsCompleted);
            var during = await composite.ReadSnapshotAsync(default);
            Assert.IsFalse(during.Sessions.Any(session => session.Id == browser.Id));
            Assert.IsFalse(during.CurrentSessionHints.Contains(browser.Id));
            Assert.AreEqual(MediaBackendCommandStatus.SessionGone,
                (await composite.ExecuteAsync(new(browser.Id, browser.BindingGeneration, MediaOperation.Play, []), default)).Status);
            Assert.AreEqual(MediaBackendCommandStatus.Completed,
                (await composite.ExecuteAsync(new(other.Id, other.BindingGeneration, MediaOperation.SkipNext, []), default)).Status);
            await composite.SetEnabledAsync("independent", true).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue((await composite.ReadSnapshotAsync(default)).Sessions.Any(static session => session.MediaProperties.Title == "Independent"));
        }
        finally
        {
            native.ReleasePolicy();
            await enabling.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task LateReadCannotRestoreSourcesAcrossRapidPolicyChanges()
    {
        var native = new FakeSourcePolicyBackend(NativeSnapshot());
        var companion = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Companion"));
        var composite = new CompositeMediaBackend(Registry(native, companion));
        await using var service = new MediaService(composite);
        await service.StartAsync();
        await WaitUntilAsync(() => service.Sessions.Length == 3);
        var original = service.Sessions.Single(static session => session.MediaProperties.Title == "Browser");
        native.BlockNextRead();
        native.Inner.SetSnapshot(NativeSnapshot(2, "Stale browser"));
        await native.ReadCaptured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var enabling = composite.SetEnabledAsync("companion", true);
        try
        {
            await native.PolicyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var disabling = composite.SetEnabledAsync("companion", false);
            var during = await composite.ReadSnapshotAsync(default);
            Assert.IsFalse(during.Sessions.Any(static session => session.MediaProperties.Source.NativeApplication?.ApplicationId == "browser.app"));
            Assert.IsFalse(enabling.IsCompleted);
            Assert.IsFalse(disabling.IsCompleted);

            native.Inner.SetSnapshot(NativeSnapshot(3, "Fresh browser"));
            native.ReleaseRead();
            await Task.WhenAll(enabling, disabling).WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => service.Sessions.Any(static session => session.MediaProperties.Title == "Fresh browser"));
            var restored = service.Sessions.Single(static session => session.MediaProperties.Title == "Fresh browser");
            Assert.AreNotEqual(original.Id, restored.Id);
            Assert.IsFalse(service.Sessions.Any(static session => session.MediaProperties.Title == "Stale browser"));
            Assert.IsEmpty(native.Policy.ExcludedApplicationIds);
        }
        finally
        {
            native.ReleaseRead();
        }
    }

    [TestMethod]
    public async Task QueuedNativeCommandAndLateArtworkCannotCrossAnOwnershipChange()
    {
        var native = new FakeSourcePolicyBackend(NativeSnapshot()) { BlockCommands = true };
        native.Inner.Artwork = new("image/png", new byte[] { 1 }, null);
        native.Inner.BlockArtwork();
        var companion = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Companion"));
        await using var composite = new CompositeMediaBackend(Registry(native, companion));
        await composite.StartAsync(default);
        var initial = await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 3);
        var browser = initial.Sessions.Single(static session => session.MediaProperties.Title == "Browser");
        var command = composite.ExecuteAsync(new(browser.Id, browser.BindingGeneration, MediaOperation.Play, []), default);
        var artwork = composite.GetArtworkAsync(browser.MediaProperties.Artwork!.Value, default).AsTask();
        try
        {
            await Task.WhenAll(native.CommandQueued.Task, native.Inner.ArtworkStarted).WaitAsync(TimeSpan.FromSeconds(5));
            await composite.SetEnabledAsync("companion", true);
            await composite.SetEnabledAsync("companion", false);
            native.ReleaseCommands();
            native.Inner.ReleaseArtwork();
            Assert.AreEqual(MediaBackendCommandStatus.SessionGone, (await command.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.IsNull(await artwork.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsEmpty(native.Inner.Commands);
            Assert.AreEqual(MediaBackendCommandStatus.SessionGone,
                (await composite.ExecuteAsync(new(browser.Id, browser.BindingGeneration, MediaOperation.Pause, []), default)).Status);
        }
        finally
        {
            native.ReleaseCommands();
            native.Inner.ReleaseArtwork();
        }
    }

    [TestMethod]
    public async Task QueuedSharedPlaySkipsSourcesExcludedAfterAdmission()
    {
        var native = new FakeSourcePolicyBackend(NativeSnapshot()) { BlockCommands = true };
        var companion = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Companion"));
        var player = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Player"));
        var registry = Registry(native, companion).Register(new("player", "Player", "Test provider", _ => player, EnabledByDefault: true));
        var composite = new CompositeMediaBackend(registry);
        await using var service = new MediaService(composite);
        service.UpdateOptions(new(PauseOtherSessionsOnPlay: true));
        await service.StartAsync();
        await WaitUntilAsync(() => service.Sessions.Length == 4);
        var browser = service.Sessions.Single(static session => session.MediaProperties.Title == "Browser");
        var selectedPlayer = service.Sessions.Single(static session => session.MediaProperties.Title == "Player");
        var previous = service.TrySubmit(new(MediaCommandTarget.ForSession(browser.Id), MediaOperation.SkipNext));
        try
        {
            await native.CommandQueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var play = service.TrySubmit(new(MediaCommandTarget.ForSession(selectedPlayer.Id), MediaOperation.Play));
            Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, play.Status);
            await composite.SetEnabledAsync("companion", true);
            native.ReleaseCommands();
            Assert.AreEqual(MediaCommandOutcomeStatus.SessionGone, (await previous.Completion!.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await play.Completion!.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.IsFalse(native.Inner.Commands.Any(static command => command.SessionId.Value == 1));
            Assert.AreEqual(MediaOperation.Play, player.Commands.Single().Operation);
        }
        finally
        {
            native.ReleaseCommands();
        }
    }

    [TestMethod]
    public async Task CompanionStartupFailureKeepsTheSavedExclusion()
    {
        var native = new FakeSourcePolicyBackend(NativeSnapshot());
        var companion = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Companion"))
        {
            StartFailure = new InvalidOperationException("Injected start failure."),
        };
        await using var composite = new CompositeMediaBackend(Registry(native, companion));
        await composite.StartAsync(default);
        await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 3);
        await composite.SetEnabledAsync("companion", true);
        Assert.IsTrue(composite.Backends[1].IsEnabled);
        Assert.AreEqual(MediaBackendLifecycleStatus.Faulted, composite.Backends[1].Status);
        Assert.IsTrue(native.Policy.ExcludedApplicationIds.Contains("browser.app"));
        Assert.AreEqual("Other", (await composite.ReadSnapshotAsync(default)).Sessions.Single().MediaProperties.Title);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task FailedPolicyOrUnacknowledgedSnapshotKeepsExcludedSourcesWithdrawn(bool failApplication)
    {
        var native = new FakeSourcePolicyBackend(NativeSnapshot());
        var companion = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Companion"));
        await using var composite = new CompositeMediaBackend(Registry(native, companion));
        await composite.StartAsync(default);
        await WaitForSnapshotAsync(composite, static snapshot => snapshot.Sessions.Length == 3);
        native.FailPolicy = failApplication;
        native.ReturnOldPolicyRevision = !failApplication;
        await composite.SetEnabledAsync("companion", true);
        Assert.AreEqual(MediaBackendLifecycleStatus.Faulted, composite.Backends[0].Status);
        Assert.IsFalse((await composite.ReadSnapshotAsync(default)).Sessions.Any(static session => session.MediaProperties.Source.NativeApplication?.ApplicationId == "browser.app"));
    }

    [TestMethod]
    public async Task ConflictingEnabledClaimsAreRejectedBeforeChangingPolicy()
    {
        var native = new FakeSourcePolicyBackend(NativeSnapshot());
        var companion = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Companion"));
        var registry = Registry(native, companion, companionEnabled: true).Register(new("alternative", "Alternative", "Test provider", _ =>
            new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Alternative")))
        {
            ReplacesSources = [new("native", "browser.app")],
        });
        await using var composite = new CompositeMediaBackend(registry);
        Assert.ThrowsExactly<InvalidOperationException>(() => { _ = composite.SetEnabledAsync("alternative", true); });
        Assert.IsFalse(composite.Backends[2].IsEnabled);
        Assert.ThrowsExactly<InvalidOperationException>(() => new CompositeMediaBackend(registry, ["native", "companion", "alternative"]));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task StartupFaultsAReplacementTargetThatCannotAcknowledgeTheSavedPolicy(bool supportsPolicy)
    {
        IMediaBackend native = supportsPolicy
            ? new FakeSourcePolicyBackend(NativeSnapshot()) { ReturnOldPolicyRevision = true }
            : new FakeMediaBackend(NativeSnapshot());
        var companion = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Companion"));
        await using var composite = new CompositeMediaBackend(Registry(native, companion, companionEnabled: true));
        await composite.StartAsync(default);
        await WaitUntilAsync(() => composite.Backends[0].Status == MediaBackendLifecycleStatus.Faulted &&
            composite.Backends[1].Status == MediaBackendLifecycleStatus.Ready);
        var snapshot = await composite.ReadSnapshotAsync(default);
        Assert.AreEqual("Companion", snapshot.Sessions.Single().MediaProperties.Title);
        Assert.AreEqual(MediaControlAvailability.Available, snapshot.Availability);
    }

    [TestMethod]
    public void SourceClaimsRequireExactIdentitiesAndExistingDistinctBackends()
    {
        static MediaBackendRegistration Registration() => new("provider", "Provider", "Test provider", _ =>
            new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Provider")));
        Assert.ThrowsExactly<ArgumentException>(() => new MediaBackendRegistry().Register(Registration() with { ReplacesSources = default }));
        Assert.ThrowsExactly<ArgumentException>(() => new MediaBackendRegistry().Register(Registration() with
        {
            ReplacesSources = [new("provider", "app")],
        }));
        Assert.ThrowsExactly<ArgumentException>(() => new MediaBackendRegistry().Register(Registration() with
        {
            ReplacesSources = [new("native", "app"), new("native", "app")],
        }));
        Assert.ThrowsExactly<ArgumentException>(() => new CompositeMediaBackend(new MediaBackendRegistry().Register(Registration() with
        {
            ReplacesSources = [new("missing", "app")],
        })));
        var policy = new MediaBackendSourcePolicy(1, ["browser.app"]);
        Assert.IsFalse(policy.ExcludedApplicationIds.Contains("Browser.App"));
        Assert.IsFalse(policy.ExcludedApplicationIds.Contains("browser.app.helper"));
    }

    [TestMethod]
    public async Task GsmtcAcceptsPolicyBeforeStartupAndRejectsDecreasingOrReusedRevisions()
    {
        await using var backend = new GsmtcBackend(NullLogger<GsmtcBackend>.Instance);
        await backend.ApplySourcePolicyAsync(new(1, ["browser.app"]), default);
        await backend.ApplySourcePolicyAsync(new(1, ["browser.app"]), default);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => backend.ApplySourcePolicyAsync(MediaBackendSourcePolicy.Empty, default));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => backend.ApplySourcePolicyAsync(new(1, ["another.app"]), default));
        await backend.ApplySourcePolicyAsync(new(2, []), default);
    }

    private static MediaBackendRegistry Registry(IMediaBackend native, FakeMediaBackend companion, bool companionEnabled = false) =>
        new MediaBackendRegistry()
            .Register(new("native", "Native", "Test provider", _ => native, EnabledByDefault: true))
            .Register(new("companion", "Companion", "Test provider", _ => companion, EnabledByDefault: companionEnabled)
            {
                ReplacesSources = [new("native", "browser.app")],
            });

    private static MediaBackendSnapshot NativeSnapshot(long revision = 1, string title = "Browser")
    {
        var snapshot = FakeMediaBackend.CreateSnapshot(revision, 1,
            (1, title, MediaPlaybackState.Playing), (2, "Other", MediaPlaybackState.Paused), (3, "Retained", MediaPlaybackState.Paused));
        return snapshot with
        {
            Sessions = [.. snapshot.Sessions.Select(session => session with
            {
                IsAvailable = session.Id.Value != 3,
                MediaProperties = session.MediaProperties with
                {
                    Source = session.MediaProperties.Source with
                    {
                        NativeApplication = new(session.Id.Value == 2 ? "browser.app.helper" : "browser.app"),
                    },
                    Artwork = new(new(session.Id.Value), 1),
                },
            })],
        };
    }

    private static async Task<MediaBackendSnapshot> WaitForSnapshotAsync(CompositeMediaBackend composite, Func<MediaBackendSnapshot, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var snapshot = await composite.ReadSnapshotAsync(timeout.Token);
            if (predicate(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}