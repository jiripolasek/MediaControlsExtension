// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using JPSoftworks.MediaControlsExtension.Media;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Pages;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Foundation;

namespace JPSoftworks.MediaControlsExtension.Presentation.Tests;

[TestClass]
public sealed class MediaSourcesPageTests
{
    private static readonly string[] AllRowProperties = ["Title", "Subtitle", "Details"];
    private static readonly string[] StatusProperties = ["Subtitle", "Details"];
    private static readonly string[] DetailProperty = ["Details"];
    private static readonly bool[] ToggleChoices = [true, false];
    private static readonly string[] RecoverySources = ["gsmtc", "gsmtc.worker", "vlc"];
    [TestMethod]
    public void PrefetchDoesNotSubscribeAndOpeningRefreshesTheExistingRows()
    {
        var service = new StatusService(State());
        using var page = Page(service);
        IListPage exposed = page;
        var row = Rows(exposed).Single();
        Assert.AreEqual(0, service.SubscriberCount);
        service.Publish(State() with { Connection = MediaBackendConnectionState.Disconnected });
        var notifications = 0;
        TypedEventHandler<object, IItemsChangedEventArgs> listener = (_, _) => notifications++;

        exposed.ItemsChanged += listener;

        Assert.AreEqual(1, service.SubscriberCount);
        Assert.AreSame(row, Rows(exposed).Single());
        StringAssert.Contains(row.Subtitle, "Disconnected");
        Assert.AreEqual(1, notifications);
        exposed.ItemsChanged -= listener;
        Assert.AreEqual(0, service.SubscriberCount);
    }

    [TestMethod]
    public void MultipleListenersShareOneServiceSubscriptionAndOnlyLastRemovalStopsIt()
    {
        var service = new StatusService(State());
        using var page = Page(service);
        TypedEventHandler<object, IItemsChangedEventArgs> listener = (_, _) => { };
        TypedEventHandler<object, IItemsChangedEventArgs> unrelated = (_, _) => { };
        page.ItemsChanged += listener;
        page.ItemsChanged += listener;
        page.ItemsChanged -= unrelated;
        Assert.AreEqual(1, service.AddCount);
        Assert.AreEqual(1, service.SubscriberCount);

        page.ItemsChanged -= listener;
        Assert.AreEqual(1, service.SubscriberCount);
        page.ItemsChanged -= listener;
        Assert.AreEqual(0, service.SubscriberCount);
        Assert.AreEqual(1, service.RemoveCount);

        var reads = service.ReadCount;
        service.Publish(State() with { AvailableSessionCount = 7 });
        Assert.AreEqual(reads, service.ReadCount);
        page.ItemsChanged += listener;
        Assert.AreEqual(2, service.AddCount);
        Assert.AreEqual("7", Fact(Rows(page).Single(), "Available sessions"));
    }

    [TestMethod]
    [DataRow(true, MediaBackendLifecycleStatus.Ready, MediaConnectionStatus.Connected, 0, "Ready", "Connected", "0 sessions")]
    [DataRow(true, MediaBackendLifecycleStatus.Ready, MediaConnectionStatus.Disconnected, 0, "Ready", "Disconnected", "0 sessions")]
    [DataRow(true, MediaBackendLifecycleStatus.Starting, MediaConnectionStatus.Connecting, 0, "Starting", "Connecting", "0 sessions")]
    [DataRow(true, MediaBackendLifecycleStatus.Ready, MediaConnectionStatus.Unknown, 1, "Ready", "Unknown", "1 session")]
    [DataRow(true, MediaBackendLifecycleStatus.Faulted, MediaConnectionStatus.Unknown, 0, "Failed", "Unknown", "0 sessions")]
    [DataRow(false, MediaBackendLifecycleStatus.Stopping, MediaConnectionStatus.Disconnected, 0, "Stopping", "Disconnected", "0 sessions")]
    [DataRow(false, MediaBackendLifecycleStatus.Disabled, MediaConnectionStatus.Disconnected, 0, "Disabled", "Disconnected", "0 sessions")]
    public void StatusDimensionsRemainIndependent(bool enabled, MediaBackendLifecycleStatus lifecycle,
        MediaConnectionStatus connection, int count, string lifecycleText, string connectionText, string countText)
    {
        var state = State() with
        {
            IsEnabled = enabled,
            Status = lifecycle,
            Connection = new(connection),
            AvailableSessionCount = count,
        };
        using var page = Page(new StatusService(state));
        var row = Rows(page).Single();

        Assert.AreEqual(enabled ? "Enabled" : "Disabled", Fact(row, "User setting"));
        Assert.AreEqual(lifecycleText, Fact(row, "Provider status"));
        Assert.AreEqual(connectionText, Fact(row, "Connection"));
        StringAssert.Contains(row.Subtitle, countText);
        Assert.IsTrue(page.ShowDetails);
    }

    [TestMethod]
    public void StateChangesUpdatePropertiesWithoutReplacingRowsOrRaisingItemsChanged()
    {
        var service = new StatusService(State());
        using var page = Page(service);
        var listChanges = 0;
        page.ItemsChanged += (_, _) => listChanges++;
        var row = Rows(page).Single();
        var items = page.GetItems();
        var propertyChanges = new List<string>();
        row.PropChanged += (_, args) => propertyChanges.Add(args.PropertyName);
        listChanges = 0;

        service.Publish(State() with { DisplayName = "Renamed source", AvailableSessionCount = 2 });

        Assert.AreSame(items, page.GetItems());
        Assert.AreSame(row, Rows(page).Single());
        Assert.AreEqual("Renamed source", row.Title);
        Assert.AreEqual("2", Fact(row, "Available sessions"));
        CollectionAssert.AreEquivalent(AllRowProperties, propertyChanges);
        Assert.AreEqual(0, listChanges);
    }

    [TestMethod]
    public void ProfileOnlyChangesUpdateDetailsAndRecoveryClearsDiagnostics()
    {
        var initial = State() with
        {
            DiagnosticMessage = "Read failed",
            Connection = new(MediaConnectionStatus.Unknown, "Read failed")
            {
                Connections = [new("personal", "Personal", MediaConnectionStatus.Unknown, "Bridge closed")],
            },
        };
        var service = new StatusService(initial);
        using var page = Page(service);
        page.ItemsChanged += (_, _) => { };
        var row = Rows(page).Single();
        Assert.AreEqual("Read failed", Fact(row, "Provider message"));
        Assert.IsFalse(row.Details!.Metadata.Any(static fact => fact.Key == "Connection message"));
        StringAssert.Contains(Fact(row, "Personal"), "Bridge closed");
        var changes = new List<string>();
        row.PropChanged += (_, args) => changes.Add(args.PropertyName);

        service.Publish(initial with
        {
            Connection = initial.Connection with
            {
                Connections = [new("personal", "Personal", MediaConnectionStatus.Disconnected, "Profile closed")],
            },
        });
        CollectionAssert.AreEqual(DetailProperty, changes);
        Assert.AreEqual("Disconnected\nProfile closed", Fact(row, "Personal"));

        service.Publish(State() with
        {
            Connection = MediaBackendConnectionState.Connected with
            {
                Connections = [new("personal", "Personal", MediaConnectionStatus.Connected)],
            },
        });
        Assert.AreEqual("Connected", Fact(row, "Personal"));
        Assert.IsFalse(row.Details!.Metadata.Any(static fact => fact.Key.EndsWith("message", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void EquivalentProfileArraysKeepDetailsAndDoNotNotify()
    {
        var initial = State() with
        {
            Connection = MediaBackendConnectionState.Connected with
            {
                Connections = [new("profile", "Personal", MediaConnectionStatus.Connected)],
            },
        };
        var service = new StatusService(initial);
        using var page = Page(service);
        page.ItemsChanged += (_, _) => { };
        var row = Rows(page).Single();
        var details = row.Details;
        var changes = 0;
        row.PropChanged += (_, _) => changes++;

        service.Publish(initial with
        {
            Connection = initial.Connection with { Connections = [new("profile", "Personal", MediaConnectionStatus.Connected)] },
        });

        Assert.AreSame(details, row.Details);
        Assert.AreEqual(0, changes);
    }

    [TestMethod]
    public void TopologyChangesPreserveSurvivingRowsAndProfileIdsStayProviderLocal()
    {
        var first = State("first") with
        {
            Connection = MediaBackendConnectionState.Connected with
            {
                Connections = [new("profile", "First profile", MediaConnectionStatus.Connected)],
            },
        };
        var second = State("second") with
        {
            Connection = MediaBackendConnectionState.Disconnected with
            {
                Connections = [new("profile", "Second profile", MediaConnectionStatus.Disconnected)],
            },
        };
        var service = new StatusService(first, second);
        using var page = Page(service);
        var notifications = 0;
        page.ItemsChanged += (_, _) => notifications++;
        var rows = Rows(page);
        notifications = 0;
        service.Publish(second, first, State("third"));

        var reordered = Rows(page);
        Assert.AreSame(rows[1], reordered[0]);
        Assert.AreSame(rows[0], reordered[1]);
        Assert.AreEqual("Disconnected", Fact(reordered[0], "Second profile"));
        Assert.AreEqual("Connected", Fact(reordered[1], "First profile"));
        Assert.AreEqual(1, notifications);

        service.Publish(first);
        Assert.AreSame(rows[0], Rows(page).Single());
        Assert.AreEqual(2, notifications);
        service.Publish();
        Assert.AreEqual(0, page.GetItems().Length);
    }

    [TestMethod]
    public void ConfigurationIsProviderSpecificAndAvailableWhileDisabled()
    {
        var configuration = new ListPage { Name = "Configure VLC" };
        var service = new StatusService(State("vlc") with { IsEnabled = false }, State("gsmtc"));
        var changes = new List<(string, bool)>();
        using var page = new MediaSourcesPage(service, (id, enabled) => changes.Add((id, enabled)),
            new Dictionary<string, ICommand> { ["vlc"] = configuration }, NullLoggerFactory.Instance);
        page.ItemsChanged += (_, _) => { };
        var items = page.GetItems();
        Assert.AreEqual(2, items.Length);
        Assert.AreSame(configuration, items[0].Command);
        Assert.IsEmpty(changes);
        var toggle = (IInvokableCommand)((ICommandContextItem)items[0].MoreCommands[0]).Command;
        Assert.AreEqual("Enable", ((ICommand)toggle).Name);
        Assert.AreEqual(CommandResultKind.KeepOpen, toggle.Invoke(page).Kind);
        CollectionAssert.AreEqual(new[] { ("vlc", true) }, changes);
        Assert.AreEqual("Enable", ((ICommand)toggle).Name);
        service.Publish(State("vlc"), State("gsmtc"));
        Assert.AreEqual("Disable", ((ICommand)toggle).Name);
        Assert.AreSame(configuration, ((ICommandContextItem)items[0].MoreCommands[1]).Command);
        Assert.IsInstanceOfType<IInvokableCommand>(items[1].Command);
        Assert.AreEqual("Disable", items[1].Command.Name);
    }

    [TestMethod]
    public void EnablementChangesRefreshTheCommandOutsideThePageLock()
    {
        var service = new StatusService(State());
        using var page = Page(service);
        page.ItemsChanged += (_, _) => { };
        var command = Rows(page).Single().Command!;
        var reentrantRead = false;
        command.PropChanged += (_, _) => reentrantRead = Task.Run(page.GetItems).Wait(TimeSpan.FromSeconds(3));
        service.Publish(State() with { IsEnabled = false });
        Assert.AreEqual("Enable", command.Name);
        Assert.IsTrue(reentrantRead);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InactiveToggleReadsPublishedStateBeforeAndAfterInvocation(bool delayedPublication)
    {
        var state = State() with { IsEnabled = false };
        var service = new StatusService(state);
        var choices = new List<bool>();
        using var page = new MediaSourcesPage(service, (_, enabled) =>
        {
            choices.Add(enabled);
            state = state with { IsEnabled = enabled };
            if (!delayedPublication) { service.Publish(state); }
        }, new Dictionary<string, ICommand>(), NullLoggerFactory.Instance);
        var row = Rows(page).Single();
        var toggle = (IInvokableCommand)row.Command!;

        Assert.AreEqual(CommandResultKind.KeepOpen, toggle.Invoke(page).Kind);
        Assert.AreEqual(delayedPublication ? "Enable" : "Disable", row.Command!.Name);
        service.Publish(state);
        Assert.AreEqual(CommandResultKind.KeepOpen, toggle.Invoke(page).Kind);

        CollectionAssert.AreEqual(ToggleChoices, choices);
        Assert.AreEqual(0, service.SubscriberCount);
        Assert.AreEqual(0, service.AddCount);
        Assert.AreEqual(delayedPublication ? "Disable" : "Enable", row.Command.Name);
        service.Publish(state);
        Assert.AreSame(row, Rows(page).Single());
        Assert.AreEqual("Enable", row.Command.Name);
    }

    [TestMethod]
    public void FailedEnablementWriteKeepsTheOriginalActionAndReportsFailure()
    {
        using var page = new MediaSourcesPage(new StatusService(State()), (_, _) => throw new IOException(),
            new Dictionary<string, ICommand>(), NullLoggerFactory.Instance);
        var command = (IInvokableCommand)Rows(page).Single().Command!;
        Assert.AreEqual(CommandResultKind.ShowToast, command.Invoke(page).Kind);
        Assert.AreEqual("Disable", ((ICommand)command).Name);
    }

    [TestMethod]
    public async Task ExclusiveSelectionRefreshesBothRowsAfterAsynchronousPublication()
    {
        var first = State("gsmtc") with { ExclusiveGroup = "windows" };
        var second = State("gsmtc.worker") with { ExclusiveGroup = "windows", IsEnabled = false };
        var service = new StatusService(first, second);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publication = Task.CompletedTask;
        async Task PublishAsync(string id, bool enabled)
        {
            await release.Task;
            service.Publish(first with { IsEnabled = id == first.Id && enabled }, second with { IsEnabled = id == second.Id && enabled });
        }

        using var page = new MediaSourcesPage(service, (id, enabled) => publication = PublishAsync(id, enabled),
            new Dictionary<string, ICommand>(), NullLoggerFactory.Instance);
        var rows = Rows(page);
        Assert.AreEqual(0, service.SubscriberCount);
        page.ItemsChanged += (_, _) => { };
        var reads = service.ReadCount;
        Assert.AreEqual("Disable", rows[0].Command!.Name);
        Assert.AreEqual(CommandResultKind.KeepOpen, ((IInvokableCommand)rows[1].Command!).Invoke(page).Kind);
        Assert.AreEqual(reads, service.ReadCount);
        Assert.AreEqual("Disable", rows[0].Command!.Name);
        Assert.AreEqual("Enable", rows[1].Command!.Name);
        release.SetResult();
        await publication.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("Enable", rows[0].Command!.Name);
        Assert.AreEqual("Disable", rows[1].Command!.Name);
        Assert.AreSame(rows[0], Rows(page)[0]);
        Assert.AreSame(rows[1], Rows(page)[1]);
    }

    [TestMethod]
    public void ConfigurationDiagnosticsAreVisibleAndClearAfterAnExplicitChoice()
    {
        var invalid = true;
        var state = State() with { IsEnabled = false, Status = MediaBackendLifecycleStatus.Disabled };
        var service = new StatusService(state);
        using var page = new MediaSourcesPage(service, (_, enabled) =>
        {
            invalid = false;
            service.Publish(state with { IsEnabled = enabled });
        }, new Dictionary<string, ICommand>(), NullLoggerFactory.Instance, _ => invalid ? "Choose one provider." : null);
        page.ItemsChanged += (_, _) => { };
        var row = Rows(page).Single();
        StringAssert.Contains(row.Subtitle, "Failed");
        Assert.IsTrue(row.Details!.Metadata.Any(fact => fact.Data is DetailsLink link && link.Text == "Choose one provider."));
        ((IInvokableCommand)row.Command!).Invoke(page);
        Assert.IsFalse(row.Details!.Metadata.Any(fact => fact.Data is DetailsLink link && link.Text == "Choose one provider."));
    }

    [TestMethod]
    [DataRow("open", false)]
    [DataRow("closed", false)]
    [DataRow("disposed", false)]
    [DataRow("open", true)]
    [DataRow("closed", true)]
    [DataRow("disposed", true)]
    public async Task SettingsRecoveryRefreshesAnOpenPageWithoutProviderStateChanges(string lifecycle, bool bindAfterRecovery)
    {
        var directory = Directory.CreateTempSubdirectory("MediaControls-page-recovery-").FullName;
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var saved = new JsonObject();
            var registry = new MediaBackendRegistry();
            var states = RecoverySources.Select(id =>
                State(id) with { IsEnabled = false, Status = MediaBackendLifecycleStatus.Disabled }).ToArray();
            foreach (var state in states)
            {
                saved[$"jpsoftworks.mediacontrols.MediaBackends.{state.Id}.Enabled"] = "false";
                registry.Register(new(state.Id, state.DisplayName, "", _ => throw new InvalidOperationException("No provider should start."), false));
            }
            File.WriteAllText(path, saved.ToJsonString());
            using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var settings = new MediaBackendSettings(registry, new SettingsStore(path, NullLogger.Instance));
            var service = new StatusService(states);
            using var page = new MediaSourcesPage(service, settings.SetEnabled, new Dictionary<string, ICommand>(),
                NullLoggerFactory.Instance, settings.GetDiagnostic);
            using var binding = bindAfterRecovery ? null : new MediaBackendSettingsBinding(settings, page, _ => { });
            TypedEventHandler<object, IItemsChangedEventArgs> listener = (_, _) => { };
            page.ItemsChanged += listener;
            var rows = Rows(page);
            foreach (var row in rows) { StringAssert.Contains(row.Subtitle, "Failed"); }
            var changed = new System.Collections.Concurrent.ConcurrentQueue<string>();
            foreach (var row in rows) { row.PropChanged += (_, args) => changed.Enqueue(args.PropertyName); }
            if (lifecycle == "closed") { page.ItemsChanged -= listener; }
            if (lifecycle == "disposed") { page.Dispose(); }
            var reads = service.ReadCount;
            var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            settings.EnabledChanged += (_, _) => recovered.TrySetResult();
            locked.Dispose();
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (bindAfterRecovery)
            {
                foreach (var row in rows) { StringAssert.Contains(row.Subtitle, "Failed"); }
            }
            using var lateBinding = bindAfterRecovery ? new MediaBackendSettingsBinding(settings, page, _ => { }) : null;

            Assert.IsEmpty(settings.EnabledIds);
            foreach (var state in states) { Assert.IsNull(settings.GetDiagnostic(state.Id)); }
            if (lifecycle != "open")
            {
                Assert.AreEqual(reads, service.ReadCount);
                Assert.IsEmpty(changed);
                if (lifecycle == "disposed") { return; }
                page.ItemsChanged += listener;
            }
            foreach (var row in rows)
            {
                StringAssert.Contains(row.Subtitle, "Disabled");
                Assert.IsFalse(row.Subtitle.Contains("Failed", StringComparison.Ordinal));
                Assert.IsFalse(row.Details!.Metadata.Any(fact => fact.Data is DetailsLink link &&
                    link.Text.Contains("Could not read", StringComparison.Ordinal)));
                Assert.AreEqual("Enable", row.Command!.Name);
            }
            Assert.HasCount(3, changed.Where(static property => property == "Subtitle"));
            Assert.HasCount(3, changed.Where(static property => property == "Details"));
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(directory));
            Assert.StartsWith("MediaControls-page-recovery-", Path.GetFileName(directory));
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ClosingRejectsAnInFlightReadAndReopeningFetchesCurrentState()
    {
        var service = new StatusService(State());
        using var page = Page(service);
        TypedEventHandler<object, IItemsChangedEventArgs> listener = (_, _) => { };
        page.ItemsChanged += listener;
        var row = Rows(page).Single();
        var blocked = service.BlockNextRead();
        var pending = Task.Run(() => service.Publish(State() with { DisplayName = "Obsolete" }));
        try
        {
            await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            page.ItemsChanged -= listener;
            service.Publish(State() with { DisplayName = "Latest" });
            blocked.Release();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual("Source", row.Title);
            page.ItemsChanged += listener;
            Assert.AreEqual("Latest", row.Title);
            Assert.AreSame(row, Rows(page).Single());
        }
        finally
        {
            blocked.Release();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OlderReadsCannotOverwriteNewerReadsOrReopenedPages(bool reopen)
    {
        var service = new StatusService(State());
        using var page = Page(service);
        TypedEventHandler<object, IItemsChangedEventArgs> listener = (_, _) => { };
        page.ItemsChanged += listener;
        var row = Rows(page).Single();
        var blocked = service.BlockNextRead();
        var pending = Task.Run(() => service.Publish(State() with { DisplayName = "Obsolete" }));
        try
        {
            await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (reopen)
            {
                page.ItemsChanged -= listener;
            }

            service.Publish(State() with { DisplayName = "Newest", AvailableSessionCount = 2 });
            if (reopen)
            {
                page.ItemsChanged += listener;
            }

            blocked.Release();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual("Newest", row.Title);
            Assert.AreEqual("2", Fact(row, "Available sessions"));
        }
        finally
        {
            blocked.Release();
        }
    }

    [TestMethod]
    public void ReentrantReadsDoNotDeadlockOrDropOtherRowNotifications()
    {
        var service = new StatusService(State("first"), State("second"));
        using var page = Page(service);
        var readsCompleted = new List<bool>();
        page.ItemsChanged += (_, _) => readsCompleted.Add(Task.Run(page.GetItems).Wait(TimeSpan.FromSeconds(3)));
        var rows = Rows(page);
        rows[0].PropChanged += (_, _) => readsCompleted.Add(Task.Run(page.GetItems).Wait(TimeSpan.FromSeconds(3)));
        var secondChanges = new List<string>();
        rows[1].PropChanged += (_, args) => secondChanges.Add(args.PropertyName);

        service.Publish(State("first") with { AvailableSessionCount = 1 }, State("second") with { AvailableSessionCount = 2 });

        CollectionAssert.AreEquivalent(StatusProperties, secondChanges);
        Assert.IsTrue(readsCompleted.Count >= 2);
        Assert.IsTrue(readsCompleted.All(static completed => completed), "A callback held the page lock.");
    }

    [TestMethod]
    public void FailingItemsSubscriberDoesNotBlockOtherSubscribers()
    {
        var service = new StatusService(State());
        using var page = Page(service);
        page.ItemsChanged += (_, _) => throw new InvalidOperationException("Subscriber failure");
        var notified = 0;
        page.ItemsChanged += (_, _) => notified++;
        service.Publish(State(), State("second"));

        Assert.AreEqual(1, notified);
        Assert.AreEqual(2, Rows(page).Length);
    }

    [TestMethod]
    public async Task DisposalDetachesWithoutDisposingTheServiceAndRejectsLateReads()
    {
        var service = new StatusService(State());
        using var page = Page(service);
        TypedEventHandler<object, IItemsChangedEventArgs> listener = (_, _) => { };
        page.ItemsChanged += listener;
        var row = Rows(page).Single();
        var blocked = service.BlockNextRead();
        var pending = Task.Run(() => service.Publish(State() with { DisplayName = "Obsolete" }));
        try
        {
            await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            page.Dispose();
            blocked.Release();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            page.ItemsChanged += listener;

            Assert.AreEqual("Source", row.Title);
            Assert.AreEqual(0, service.SubscriberCount);
            Assert.AreEqual(0, service.DisposeCount);
            Assert.AreEqual(0, page.GetItems().Length);
            Assert.AreEqual(1, service.RemoveCount);
        }
        finally
        {
            blocked.Release();
        }
    }

    private static MediaSourcesPage Page(StatusService service) =>
        new(service, (_, _) => { }, new Dictionary<string, ICommand>(), NullLoggerFactory.Instance);
    private static MediaSourceStatusItem[] Rows(IListPage page) => page.GetItems().OfType<MediaSourceStatusItem>().ToArray();
    private static string Fact(MediaSourceStatusItem row, string label) =>
        ((DetailsLink)row.Details!.Metadata.Single(fact => fact.Key == label).Data!).Text;

    private static MediaBackendState State(string id = "source") => new(id, true, MediaBackendLifecycleStatus.Ready, null)
    {
        DisplayName = "Source",
        Connection = MediaBackendConnectionState.Connected,
    };

    private sealed class StatusService(params MediaBackendState[] initial) : IMediaService
    {
        private readonly Lock _gate = new();
        private ImmutableArray<MediaBackendState> _backends = [.. initial];
        private EventHandler? _changed;
        private BlockedRead? _blocked;
        public int AddCount { get; private set; }
        public int RemoveCount { get; private set; }
        public int ReadCount { get; private set; }
        public int DisposeCount { get; private set; }

        public int SubscriberCount
        {
            get
            {
                lock (this._gate)
                {
                    return this._changed?.GetInvocationList().Length ?? 0;
                }
            }
        }

        public event EventHandler? BackendsChanged
        {
            add { lock (this._gate) { this._changed += value; this.AddCount++; } }
            remove { lock (this._gate) { this._changed -= value; this.RemoveCount++; } }
        }

        public ImmutableArray<MediaBackendState> Backends
        {
            get
            {
                ImmutableArray<MediaBackendState> captured;
                BlockedRead? blocked;
                lock (this._gate)
                {
                    this.ReadCount++;
                    captured = this._backends;
                    blocked = this._blocked;
                    this._blocked = null;
                }

                if (blocked is not null)
                {
                    blocked.Entered.TrySetResult();
                    blocked.Released.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                }

                return captured;
            }
        }

        public void Publish(params MediaBackendState[] states)
        {
            EventHandler? handlers;
            lock (this._gate)
            {
                this._backends = [.. states];
                handlers = this._changed;
            }

            handlers?.Invoke(this, EventArgs.Empty);
        }

        public BlockedRead BlockNextRead()
        {
            lock (this._gate)
            {
                return this._blocked = new();
            }
        }

        public event EventHandler? SessionsChanged { add { } remove { } }
        public event EventHandler? CurrentSessionChanged { add { } remove { } }
        public event EventHandler? StatusChanged { add { } remove { } }
        public ImmutableArray<MediaSession> Sessions => [];
        public MediaSession? CurrentSession => null;
        public MediaServiceStatus Status => MediaServiceStatus.Ready;
        public MediaControlAvailability Availability => MediaControlAvailability.Available;
        public Task StartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public MediaCommandSubmission TrySubmit(MediaCommand command) => throw new NotSupportedException();
        public ValueTask<MediaArtworkContent?> GetArtworkAsync(MediaArtworkKey key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public void UpdateOptions(MediaServiceOptions options) => throw new NotSupportedException();
        public void Dispose() => this.DisposeCount++;
        public ValueTask DisposeAsync() { this.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class BlockedRead
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => this.Released.TrySetResult();
    }
}