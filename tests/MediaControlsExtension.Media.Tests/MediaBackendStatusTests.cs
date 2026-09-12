// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using System.Threading.Channels;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.State;
using JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class MediaBackendStatusTests
{
    [TestMethod]
    public async Task SlowStartupPublishesConnectingBeforeConnectedEmpty()
    {
        var backend = new FakeMediaBackend(Snapshot(1, MediaConnectionStatus.Connected));
        backend.BlockStart();
        var registry = new MediaBackendRegistry().Register(new("browser", "Browser", "Test", _ => backend));
        await using var composite = new CompositeMediaBackend(registry);
        await using var service = new MediaService(composite);
        using var observer = new BackendObserver(service);
        await service.StartAsync();
        var disabled = await observer.WaitForAsync("browser", static state => !state.IsEnabled);
        Assert.AreEqual(MediaBackendLifecycleStatus.Disabled, disabled.Status);
        Assert.AreEqual(MediaConnectionStatus.Disconnected, disabled.Connection.Status);
        var enabling = composite.SetEnabledAsync("browser", true);
        try
        {
            await backend.StartStarted.WaitAsync(TimeSpan.FromSeconds(5));
            var starting = await observer.WaitForAsync("browser", static state => state.Status == MediaBackendLifecycleStatus.Starting);
            Assert.IsTrue(starting.IsEnabled);
            Assert.AreEqual("Browser", starting.DisplayName);
            Assert.AreEqual(MediaConnectionStatus.Connecting, starting.Connection.Status);
            Assert.AreEqual(0, starting.AvailableSessionCount);
            Assert.IsFalse(enabling.IsCompleted);

            backend.ReleaseStart();
            await enabling.WaitAsync(TimeSpan.FromSeconds(5));
            var ready = await observer.WaitForAsync("browser", static state => state.Status == MediaBackendLifecycleStatus.Ready);
            Assert.AreEqual(MediaConnectionStatus.Connected, ready.Connection.Status);
            Assert.AreEqual(0, ready.AvailableSessionCount);
            Assert.IsTrue(service.Sessions.IsEmpty);
        }
        finally
        {
            backend.ReleaseStart();
        }
    }

    [TestMethod]
    public async Task ConnectionChangesNotifyWithoutSessionsOrAnOverallStatusChange()
    {
        var backend = new FakeMediaBackend(Snapshot(1, MediaConnectionStatus.Disconnected));
        await using var service = new MediaService(new CompositeMediaBackend(Registry(backend)));
        using var observer = new BackendObserver(service);
        await service.StartAsync();
        await observer.WaitForAsync("provider0", static state => state.Status == MediaBackendLifecycleStatus.Ready);
        var status = service.Status;
        Publish(backend, Snapshot(2, MediaConnectionStatus.Connecting));
        await observer.WaitForAsync("provider0", static state => state.Connection.Status == MediaConnectionStatus.Connecting);
        Publish(backend, Snapshot(3, MediaConnectionStatus.Connected));
        var connected = await observer.WaitForAsync("provider0", static state => state.Connection.Status == MediaConnectionStatus.Connected);
        Assert.AreEqual(0, connected.AvailableSessionCount);
        Assert.IsTrue(service.Sessions.IsEmpty);

        Publish(backend, Snapshot(4, MediaConnectionStatus.Connected, withSession: true));
        await observer.WaitForAsync("provider0", static state => state.AvailableSessionCount == 1);
        Publish(backend, Snapshot(5, MediaConnectionStatus.Connected));
        await observer.WaitForAsync("provider0", static state => state.AvailableSessionCount == 0);
        Publish(backend, Snapshot(6, MediaConnectionStatus.Disconnected) with
        {
            Connection = new(MediaConnectionStatus.Disconnected, "Browser closed"),
        });
        var disconnected = await observer.WaitForAsync("provider0", static state => state.Connection.Status == MediaConnectionStatus.Disconnected);
        Assert.AreEqual("Browser closed", disconnected.Connection.DiagnosticMessage);
        Assert.AreEqual(MediaBackendLifecycleStatus.Ready, disconnected.Status);
        Assert.AreEqual(status, service.Status);
        Assert.IsTrue(disconnected.IsEnabled);
        Assert.AreEqual(0, backend.DisposeCount);
    }

    [TestMethod]
    public async Task ProfileChangesRemainScopedToTheirProvider()
    {
        var first = new FakeMediaBackend(Snapshot(1, MediaConnectionStatus.Connected, withSession: true) with
        {
            Connection = Profiles(new MediaConnectionState("profile", "First profile", MediaConnectionStatus.Connected)),
        });
        var secondSnapshot = Snapshot(1, MediaConnectionStatus.Connected) with
        {
            Connection = Profiles(new MediaConnectionState("profile", "Second profile", MediaConnectionStatus.Connected),
                new("work", "Work", MediaConnectionStatus.Connected)),
        };
        var second = new FakeMediaBackend(secondSnapshot);
        await using var service = new MediaService(new CompositeMediaBackend(Registry(first, second)));
        using var observer = new BackendObserver(service);
        await service.StartAsync();
        await observer.WaitForStatesAsync(static states => states.Length == 2 && states.All(state => state.Status == MediaBackendLifecycleStatus.Ready));
        var current = service.CurrentSession;
        Publish(second, secondSnapshot with
        {
            Revision = 2,
            Connection = Profiles(new MediaConnectionState("profile", "Second profile", MediaConnectionStatus.Disconnected, "Profile closed"),
                new("work", "Work", MediaConnectionStatus.Connected)),
        });
        var changed = await observer.WaitForAsync("provider1", static state => state.Connection.Connections[0].Status == MediaConnectionStatus.Disconnected);
        Assert.AreEqual(MediaConnectionStatus.Connected, changed.Connection.Status);
        Assert.AreEqual("Profile closed", changed.Connection.Connections[0].DiagnosticMessage);
        Assert.AreEqual(MediaConnectionStatus.Connected, service.Backends[0].Connection.Connections[0].Status);
        Assert.AreEqual("First profile", service.Backends[0].Connection.Connections[0].DisplayName);
        Assert.AreSame(current, service.CurrentSession);
        Assert.IsTrue(current!.IsAvailable);
    }

    [TestMethod]
    public async Task ReadFailureMarksOldConnectionDetailsUnknownAndRecoveryClearsTheError()
    {
        var snapshot = Snapshot(1, MediaConnectionStatus.Connected, withSession: true) with
        {
            Connection = Profiles(new MediaConnectionState("profile", "Profile", MediaConnectionStatus.Connected)),
        };
        var backend = new FakeMediaBackend(snapshot);
        var healthy = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Healthy"));
        await using var service = new MediaService(new CompositeMediaBackend(Registry(backend, healthy)));
        using var observer = new BackendObserver(service);
        await service.StartAsync();
        await observer.WaitForStatesAsync(static states => states.Length == 2 && states.All(state => state.Status == MediaBackendLifecycleStatus.Ready));
        backend.SnapshotFailure = new InvalidOperationException("Observation failed");
        backend.Signal(MediaBackendSignal.BackendsChanged);
        var faulted = await observer.WaitForAsync("provider0", static state => state.Status == MediaBackendLifecycleStatus.Faulted);
        Assert.AreEqual(MediaConnectionStatus.Unknown, faulted.Connection.Status);
        Assert.AreEqual("Observation failed", faulted.DiagnosticMessage);
        Assert.AreEqual("Observation failed", faulted.Connection.DiagnosticMessage);
        Assert.AreEqual(MediaConnectionStatus.Unknown, faulted.Connection.Connections[0].Status);
        Assert.AreEqual(0, faulted.AvailableSessionCount);
        Assert.AreEqual(MediaServiceStatus.Ready, service.Status);
        var healthySession = service.Sessions.Single(static session => session.MediaProperties.Title == "Healthy");
        var command = service.TrySubmit(new(MediaCommandTarget.ForSession(healthySession.Id), MediaOperation.SkipNext));
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, command.Status);
        Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await command.Completion!.WaitAsync(TimeSpan.FromSeconds(5))).Status);

        backend.SnapshotFailure = null;
        Publish(backend, snapshot with { Revision = 2 });
        var recovered = await observer.WaitForAsync("provider0", static state => state.Status == MediaBackendLifecycleStatus.Ready);
        Assert.AreEqual(MediaConnectionStatus.Connected, recovered.Connection.Status);
        Assert.AreEqual(MediaConnectionStatus.Connected, recovered.Connection.Connections[0].Status);
        Assert.IsNull(recovered.DiagnosticMessage);
        Assert.IsNull(recovered.Connection.DiagnosticMessage);
        Assert.AreEqual(1, recovered.AvailableSessionCount);
        Assert.AreEqual(0, backend.DisposeCount);
    }

    [TestMethod]
    [DataRow("start", MediaConnectionStatus.Disconnected)]
    [DataRow("watch", MediaConnectionStatus.Unknown)]
    [DataRow("dispose", MediaConnectionStatus.Unknown)]
    public async Task LifecycleFailuresAreObservableWithoutCallingTheProviderAgain(string operation, MediaConnectionStatus expectedConnection)
    {
        var backend = new FakeMediaBackend(Snapshot(1, MediaConnectionStatus.Connected));
        if (operation == "start")
        {
            backend.StartFailure = new InvalidOperationException("Startup failed");
        }

        await using var composite = new CompositeMediaBackend(Registry(backend));
        await using var service = new MediaService(composite);
        using var observer = new BackendObserver(service);
        await service.StartAsync();
        if (operation != "start")
        {
            await observer.WaitForAsync("provider0", static state => state.Status == MediaBackendLifecycleStatus.Ready);
            if (operation == "watch")
            {
                backend.CompleteWatch();
            }
            else
            {
                backend.FailDisposal = true;
                await composite.SetEnabledAsync("provider0", false);
            }
        }

        var faulted = await observer.WaitForAsync("provider0", state => state.Status == MediaBackendLifecycleStatus.Faulted &&
            state.Connection.Status == expectedConnection);
        Assert.IsFalse(string.IsNullOrWhiteSpace(faulted.DiagnosticMessage));
        Assert.AreEqual(operation != "dispose", faulted.IsEnabled);
        Assert.AreEqual(0, faulted.AvailableSessionCount);
    }

    [TestMethod]
    public async Task DisablePublishesStoppingAndClearsConnectionsBeforeCommandsDrain()
    {
        var backend = new FakeMediaBackend(Snapshot(1, MediaConnectionStatus.Connected, withSession: true) with
        {
            Connection = Profiles(new MediaConnectionState("profile", "Profile", MediaConnectionStatus.Connected)),
        }) { IgnoreCommandCancellation = true };
        backend.BlockCommands();
        await using var composite = new CompositeMediaBackend(Registry(backend));
        await using var service = new MediaService(composite);
        using var observer = new BackendObserver(service);
        await service.StartAsync();
        await observer.WaitForAsync("provider0", static state => state.AvailableSessionCount == 1);
        var command = service.TrySubmit(new(MediaCommandTarget.CurrentSession, MediaOperation.SkipNext));
        Assert.AreEqual(MediaCommandSubmissionStatus.Accepted, command.Status);
        await backend.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var disabling = composite.SetEnabledAsync("provider0", false);
            var stopping = await observer.WaitForAsync("provider0", static state => state.Status == MediaBackendLifecycleStatus.Stopping);
            Assert.IsFalse(stopping.IsEnabled);
            Assert.AreEqual(MediaConnectionStatus.Disconnected, stopping.Connection.Status);
            Assert.IsTrue(stopping.Connection.Connections.IsEmpty);
            Assert.AreEqual(0, stopping.AvailableSessionCount);
            Assert.IsFalse(disabling.IsCompleted);
            backend.ReleaseCommands();
            await disabling.WaitAsync(TimeSpan.FromSeconds(5));
            await observer.WaitForAsync("provider0", static state => state.Status == MediaBackendLifecycleStatus.Disabled);
            Assert.AreEqual(1, backend.DisposeCount);
        }
        finally
        {
            backend.ReleaseCommands();
        }
    }

    [TestMethod]
    public async Task LateReadCannotRestoreRetiredConnectionsDuringReenable()
    {
        var oldBackend = new FakeMediaBackend(Snapshot(1, MediaConnectionStatus.Connected)) { IgnoreSnapshotCancellation = true };
        var replacement = new FakeMediaBackend(Snapshot(1, MediaConnectionStatus.Disconnected));
        var created = 0;
        var registry = new MediaBackendRegistry().Register(new("browser", "Browser", "Test",
            _ => Interlocked.Increment(ref created) == 1 ? oldBackend : replacement, EnabledByDefault: true));
        await using var composite = new CompositeMediaBackend(registry);
        await using var service = new MediaService(composite);
        using var observer = new BackendObserver(service);
        await service.StartAsync();
        await observer.WaitForAsync("browser", static state => state.Status == MediaBackendLifecycleStatus.Ready);
        oldBackend.BlockSnapshotReads();
        Publish(oldBackend, Snapshot(2, MediaConnectionStatus.Connected) with
        {
            Connection = Profiles(new MediaConnectionState("retired", "Old profile", MediaConnectionStatus.Connected)),
        });
        await oldBackend.SnapshotReadStarted.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var disabling = composite.SetEnabledAsync("browser", false);
            await observer.WaitForAsync("browser", static state => state.Status == MediaBackendLifecycleStatus.Stopping);
            var enabling = composite.SetEnabledAsync("browser", true);
            oldBackend.ReleaseSnapshotReads();
            await Task.WhenAll(disabling, enabling).WaitAsync(TimeSpan.FromSeconds(5));
            var ready = await observer.WaitForAsync("browser", static state => state.Status == MediaBackendLifecycleStatus.Ready);
            Assert.AreEqual(MediaConnectionStatus.Disconnected, ready.Connection.Status);
            Assert.IsTrue(ready.Connection.Connections.IsEmpty);
            Assert.AreEqual(2, created);
            Assert.AreEqual(1, oldBackend.DisposeCount);
            Assert.AreEqual(MediaConnectionStatus.Disconnected, composite.Backends[0].Connection.Status);
        }
        finally
        {
            oldBackend.ReleaseSnapshotReads();
        }
    }

    [TestMethod]
    [DataRow(MediaControlAvailability.Available, 1)]
    [DataRow(MediaControlAvailability.CircuitOpen, 0)]
    [DataRow(MediaControlAvailability.Unavailable, 0)]
    public async Task ConnectionStatusIsIndependentOfControlAvailability(MediaControlAvailability availability, int availableSessions)
    {
        var backend = new FakeMediaBackend(Snapshot(1, MediaConnectionStatus.Connected, withSession: true) with { Availability = availability });
        await using var service = new MediaService(new CompositeMediaBackend(Registry(backend)));
        using var observer = new BackendObserver(service);
        await service.StartAsync();
        var ready = await observer.WaitForAsync("provider0", static state => state.Status == MediaBackendLifecycleStatus.Ready);
        Assert.AreEqual(MediaConnectionStatus.Connected, ready.Connection.Status);
        Assert.AreEqual(availableSessions, ready.AvailableSessionCount);
    }

    [TestMethod]
    public async Task UnreportedConnectionStatusIsNotInferredFromAvailableSessions()
    {
        var backend = new FakeMediaBackend(Snapshot(1, MediaConnectionStatus.Unknown, withSession: true));
        await using var service = new MediaService(new CompositeMediaBackend(Registry(backend)));
        using var observer = new BackendObserver(service);
        await service.StartAsync();
        var ready = await observer.WaitForAsync("provider0", static state => state.Status == MediaBackendLifecycleStatus.Ready);
        Assert.AreEqual(MediaConnectionStatus.Unknown, ready.Connection.Status);
        Assert.AreEqual(1, ready.AvailableSessionCount);
    }

    [TestMethod]
    public void EqualConnectionContentsDoNotGenerateDuplicateNotifications()
    {
        var catalog = new MediaSessionCatalog();
        var backend = new MediaBackendState("browser", true, MediaBackendLifecycleStatus.Ready, null)
        {
            Connection = Profiles(new MediaConnectionState("profile", "Profile", MediaConnectionStatus.Connected)),
        };
        var snapshot = new MediaServiceSnapshot(1, MediaServiceStatus.Ready, [], null, MediaControlAvailability.Available)
        {
            Backends = [backend],
        };
        Assert.IsTrue(catalog.Apply(snapshot).ServiceChanges.HasFlag(MediaServiceChanges.Backends));
        var original = catalog.State.Backends;
        var equivalent = backend with { Connection = Profiles(backend.Connection.Connections[0] with { }) };
        Assert.AreEqual(MediaServiceChanges.None, catalog.Apply(snapshot with { Revision = 2, Backends = [equivalent] }).ServiceChanges);
        Assert.IsTrue(original == catalog.State.Backends);
        var changed = backend with { Connection = Profiles(backend.Connection.Connections[0] with { DiagnosticMessage = "Updated" }) };
        Assert.IsTrue(catalog.Apply(snapshot with { Revision = 3, Backends = [changed] }).ServiceChanges.HasFlag(MediaServiceChanges.Backends));
    }

    [TestMethod]
    public async Task SubscriberFailureDoesNotBlockOtherObserversAndDisposalClearsPublishedState()
    {
        var backend = new FakeMediaBackend(Snapshot(1, MediaConnectionStatus.Connected));
        await using var service = new MediaService(new CompositeMediaBackend(Registry(backend)));
        service.BackendsChanged += (_, _) => throw new InvalidOperationException("Subscriber failed");
        using var observer = new BackendObserver(service);
        await service.StartAsync();
        await observer.WaitForAsync("provider0", static state => state.Status == MediaBackendLifecycleStatus.Ready);
        Publish(backend, Snapshot(2, MediaConnectionStatus.Disconnected));
        await observer.WaitForAsync("provider0", static state => state.Connection.Status == MediaConnectionStatus.Disconnected);
        await service.DisposeAsync();
        await observer.WaitForStatesAsync(static states => states.IsEmpty);
        Assert.IsTrue(service.Backends.IsEmpty);
    }

    [TestMethod]
    [DataRow("duplicate")]
    [DataRow("default")]
    [DataRow("invalid")]
    public async Task MalformedConnectionSnapshotsFaultOnlyTheirOwner(string problem)
    {
        var connection = problem switch
        {
            "duplicate" => Profiles(new MediaConnectionState("same", "First", MediaConnectionStatus.Connected), new("same", "Second", MediaConnectionStatus.Connected)),
            "default" => MediaBackendConnectionState.Connected with { Connections = default },
            _ => new MediaBackendConnectionState((MediaConnectionStatus)99),
        };
        var bad = new FakeMediaBackend(Snapshot(1, MediaConnectionStatus.Connected) with { Connection = connection });
        var healthy = new FakeMediaBackend(Snapshot(1, MediaConnectionStatus.Connected, withSession: true));
        await using var service = new MediaService(new CompositeMediaBackend(Registry(bad, healthy)));
        using var observer = new BackendObserver(service);
        await service.StartAsync();
        await observer.WaitForStatesAsync(static states => states.Length == 2 &&
            states[0].Status == MediaBackendLifecycleStatus.Faulted && states[1].Status == MediaBackendLifecycleStatus.Ready);
        Assert.AreEqual(MediaConnectionStatus.Unknown, service.Backends[0].Connection.Status);
        Assert.AreEqual(MediaServiceStatus.Ready, service.Status);
        Assert.AreEqual(1, service.Backends[1].AvailableSessionCount);
    }

    private static MediaBackendConnectionState Profiles(params MediaConnectionState[] connections) =>
        MediaBackendConnectionState.Connected with { Connections = [.. connections] };

    private static MediaBackendSnapshot Snapshot(long revision, MediaConnectionStatus status, bool withSession = false)
    {
        var snapshot = FakeMediaBackend.CreateSnapshot(revision, "Player");
        return snapshot with
        {
            Sessions = withSession ? snapshot.Sessions : [],
            CurrentSessionHints = withSession ? snapshot.CurrentSessionHints : [],
            Connection = new(status),
        };
    }

    private static void Publish(FakeMediaBackend backend, MediaBackendSnapshot snapshot)
    {
        backend.SetSnapshotWithoutSignal(snapshot);
        backend.Signal(MediaBackendSignal.BackendsChanged);
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

    private sealed class BackendObserver : IDisposable
    {
        private readonly IMediaService _service;
        private readonly Channel<ImmutableArray<MediaBackendState>> _updates = Channel.CreateUnbounded<ImmutableArray<MediaBackendState>>();

        public BackendObserver(IMediaService service)
        {
            this._service = service;
            service.BackendsChanged += this.OnChanged;
            this._updates.Writer.TryWrite(service.Backends);
        }

        public async Task<MediaBackendState> WaitForAsync(string id, Func<MediaBackendState, bool> predicate)
        {
            var states = await this.WaitForStatesAsync(states => states.Any(state => state.Id == id && predicate(state)));
            return states.Single(state => state.Id == id);
        }

        public async Task<ImmutableArray<MediaBackendState>> WaitForStatesAsync(Func<ImmutableArray<MediaBackendState>, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var states in this._updates.Reader.ReadAllAsync(timeout.Token))
            {
                if (predicate(states))
                {
                    return states;
                }
            }

            throw new InvalidOperationException("Provider notifications ended.");
        }

        private void OnChanged(object? sender, EventArgs args) => this._updates.Writer.TryWrite(this._service.Backends);

        public void Dispose() => this._service.BackendsChanged -= this.OnChanged;
    }
}