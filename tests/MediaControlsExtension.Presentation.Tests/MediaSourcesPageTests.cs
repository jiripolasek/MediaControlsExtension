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
    private static readonly string[] AllRowProperties = ["Title", "Tags", "Details"];
    private static readonly string[] StatusProperties = ["Tags", "Details"];
    private static readonly string[] DetailProperty = ["Details"];
    private static readonly bool[] ToggleChoices = [true, false];
    private static readonly string[] RecoverySources = ["gsmtc", "gsmtc.worker", "vlc"];
    private static readonly string[] SetupSections = ["Recommended", "Optional players", "Optional players", "Troubleshooting"];

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, true, false)]
    [DataRow(false, true, true)]
    [DataRow(true, false, false)]
    [DataRow(true, true, false)]
    [DataRow(true, true, true)]
    [DataRow(true, false, true)]
    public void SourceActionsAreUniqueAndConsistent(bool configurable, bool enabled, bool faulted)
    {
        var configuration = new MediaBackendConfigurationPage("vlc", "VLC", new Settings(), () => { }, NullLoggerFactory.Instance);
        var state = State(configurable ? "vlc" : "itunes") with
        {
            IsEnabled = enabled,
            Status = faulted ? MediaBackendLifecycleStatus.Faulted : MediaBackendLifecycleStatus.Ready,
        };
        using var page = new MediaSourcesPage(new StatusService(state), (_, _) => { },
            configurable ? new Dictionary<string, ICommand> { ["vlc"] = configuration } : new Dictionary<string, ICommand>(),
            NullLoggerFactory.Instance);
        var row = Rows(page).Single();
        ICommand[] menuCommands = [row.Command!, .. row.MoreCommands.OfType<ICommandContextItem>().Select(static item => item.Command)];
        var expectedNames = new List<string>();
        if (configurable) { expectedNames.Add("Configure"); }
        expectedNames.Add(enabled ? "Disable" : "Enable");
        if (enabled && faulted) { expectedNames.Add("Retry"); }

        CollectionAssert.AreEqual(expectedNames, menuCommands.Select(static command => command.Name).ToArray());
        Assert.AreEqual(menuCommands.Length, menuCommands.Select(static command => command.Id).Distinct().Count());
        var detailsCommands = Assert.IsInstanceOfType<DetailsCommands>(row.Details!.Metadata.Single(fact => fact.Key == "Actions").Data);
        Assert.IsNotNull(detailsCommands.Commands);
        CollectionAssert.AreEqual(menuCommands, detailsCommands.Commands);
        foreach (var command in menuCommands)
        {
            Assert.IsNotNull(command.Icon);
            Assert.IsFalse(string.IsNullOrEmpty(command.Icon.Light.Icon), command.Name);
            Assert.IsFalse(string.IsNullOrEmpty(command.Icon.Dark.Icon), command.Name);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ActionIconsFollowEnablementInMenusAndDetails(bool configurable)
    {
        var state = State(configurable ? "vlc" : "itunes");
        var service = new StatusService(state);
        var configuration = new MediaBackendConfigurationPage("vlc", "VLC", new Settings(), () => { }, NullLoggerFactory.Instance);
        using var page = new MediaSourcesPage(service, (_, _) => { },
            configurable ? new Dictionary<string, ICommand> { ["vlc"] = configuration } : new Dictionary<string, ICommand>(),
            NullLoggerFactory.Instance);
        page.ItemsChanged += (_, _) => { };
        var row = Rows(page).Single();
        var sourceIcon = row.Icon;
        var toggle = configurable ? row.MoreCommands.OfType<ICommandContextItem>().Single().Command : row.Command!;
        var disableIcon = toggle.Icon;
        var iconChanges = 0;
        toggle.PropChanged += (_, args) => { if (args.PropertyName == "Icon") { iconChanges++; } };

        service.Publish(state with { IsEnabled = false, Status = MediaBackendLifecycleStatus.Disabled });

        Assert.AreEqual("Enable", toggle.Name);
        Assert.IsNotNull(toggle.Icon);
        Assert.AreNotEqual(disableIcon!.Light.Icon, toggle.Icon.Light.Icon);
        Assert.AreEqual(1, iconChanges);
        var detailsCommands = Assert.IsInstanceOfType<DetailsCommands>(row.Details!.Metadata.Single(fact => fact.Key == "Actions").Data);
        Assert.IsNotNull(detailsCommands.Commands);
        Assert.Contains(toggle, detailsCommands.Commands);
        Assert.AreSame(sourceIcon, row.Icon);

        service.Publish(state);

        Assert.AreEqual("Disable", toggle.Name);
        Assert.AreSame(disableIcon, toggle.Icon);
        Assert.AreEqual(2, iconChanges);
        Assert.AreSame(sourceIcon, row.Icon);
    }

    [TestMethod]
    public void StatusColorsDistinguishEnablementAndAttentionWithoutIcons()
    {
        var state = State("itunes");
        var service = new StatusService(state);
        using var page = Page(service);
        page.ItemsChanged += (_, _) => { };
        var row = Rows(page).Single();
        var enabled = row.Tags.Single();
        Assert.StartsWith("Enabled:", enabled.ToolTip);

        service.Publish(state with { IsEnabled = false, Status = MediaBackendLifecycleStatus.Disabled });
        var disabled = row.Tags.Single();
        Assert.AreEqual("Off", disabled.Text);
        Assert.StartsWith("Disabled:", disabled.ToolTip);

        service.Publish(state with { Status = MediaBackendLifecycleStatus.Faulted });
        var attention = row.Tags.Single();
        Assert.AreEqual("Needs attention", attention.Text);
        Assert.AreNotEqual(enabled.Background, disabled.Background);
        Assert.AreNotEqual(enabled.Background, attention.Background);
        Assert.AreNotEqual(disabled.Background, attention.Background);
        foreach (var tag in new[] { enabled, disabled, attention })
        {
            Assert.IsTrue(tag.Background.HasValue);
            Assert.IsTrue(tag.Foreground.HasValue);
            Assert.IsTrue(string.IsNullOrEmpty(tag.Icon?.Light.Icon));
        }

        service.Publish(state with { Connection = MediaBackendConnectionState.Disconnected });
        Assert.AreEqual(enabled.Background, row.Tags.Single().Background);
        Assert.AreEqual("Waiting for iTunes", row.Tags.Single().Text);
    }

    [TestMethod]
    public void SourceIconsStaySeparateFromActionIcons()
    {
        var service = new StatusService(State("gsmtc.worker"), State("itunes"), State("vlc"), State("gsmtc"));
        using var page = Page(service);
        page.ItemsChanged += (_, _) => { };
        var rows = Rows(page);
        var sourceIcons = rows.Select(static row => row.Icon).ToArray();
        Assert.EndsWith("Windows.svg", sourceIcons[0]!.Light.Icon!);
        Assert.AreSame(sourceIcons[0], sourceIcons[3]);
        Assert.EndsWith("ITunes.svg", sourceIcons[1]!.Light.Icon!);
        Assert.EndsWith("Vlc.svg", sourceIcons[2]!.Light.Icon!);

        service.Publish(State("gsmtc.worker") with { IsEnabled = false }, State("itunes") with { IsEnabled = false },
            State("vlc") with { IsEnabled = false }, State("gsmtc") with { IsEnabled = false });

        CollectionAssert.AreEqual(sourceIcons, rows.Select(static row => row.Icon).ToArray());
        foreach (var row in rows)
        {
            Assert.AreNotEqual(row.Icon!.Light.Icon, row.Command!.Icon!.Light.Icon);
        }
    }

    [TestMethod]
    public void SourcesShowDescriptionsGuidanceAndActionsInSetupOrder()
    {
        var service = new StatusService(State("gsmtc"), State("itunes"), State("gsmtc.worker"), State("vlc"));
        var configuration = new ListPage { Name = "Configure" };
        using var page = new MediaSourcesPage(service, (_, _) => { },
            new Dictionary<string, ICommand> { ["vlc"] = configuration }, NullLoggerFactory.Instance,
            sourceDescriptions: new Dictionary<string, string> { ["itunes"] = "Desktop iTunes only" });
        var rows = Rows(page);

        CollectionAssert.AreEqual(SetupSections,
            rows.Select(static row => row.Section).ToArray());
        Assert.AreEqual("Desktop iTunes only", rows[1].Subtitle);
        Assert.Contains("Apple Music", rows[1].Details!.Body);
        Assert.Contains("Open desktop iTunes", rows[1].Details!.Body);
        Assert.Contains("Save and enable", rows[2].Details!.Body);
        Assert.Contains("To switch back, enable Windows media under Recommended.", rows[3].Details!.Body);
        var actions = Assert.IsInstanceOfType<DetailsCommands>(rows[2].Details!.Metadata.Single(fact => fact.Key == "Actions").Data);
        Assert.IsNotNull(actions.Commands);
        Assert.Contains(configuration, actions.Commands);
        Assert.Contains(Assert.IsInstanceOfType<ICommandContextItem>(rows[2].MoreCommands[0]).Command, actions.Commands);
    }

    [TestMethod]
    public void ITunesWaitingForTheAppIsDifferentFromAFailedSource()
    {
        using var page = Page(new StatusService(State("itunes") with { Connection = MediaBackendConnectionState.Disconnected }));
        var row = Rows(page).Single();

        Assert.AreEqual("Waiting for iTunes", row.Tags.Single().Text);
        Assert.IsFalse(row.MoreCommands.OfType<ICommandContextItem>().Any(item => item.Command.Name == "Retry"));
    }

    [TestMethod]
    public void RetryKeepsTheSourceEnabledAndDisappearsAfterRecovery()
    {
        var state = State("itunes") with { Status = MediaBackendLifecycleStatus.Faulted, DiagnosticMessage = "Worker stopped" };
        var service = new StatusService(state);
        var choices = new List<(string, bool)>();
        using var page = new MediaSourcesPage(service, (id, enabled) =>
        {
            choices.Add((id, enabled));
            service.Publish(State("itunes"));
        }, new Dictionary<string, ICommand>(), NullLoggerFactory.Instance);
        page.ItemsChanged += (_, _) => { };
        var row = Rows(page).Single();
        var retry = Assert.IsInstanceOfType<IInvokableCommand>(row.MoreCommands.OfType<ICommandContextItem>().Single(item => item.Command.Name == "Retry").Command);
        var unlocked = true;
        row.PropChanged += (_, _) => unlocked &= Task.Run(page.GetItems).Wait(TimeSpan.FromSeconds(3));

        Assert.AreEqual(CommandResultKind.KeepOpen, retry.Invoke(page).Kind);

        CollectionAssert.AreEqual(new[] { ("itunes", true) }, choices);
        Assert.AreEqual("Disable", row.Command!.Name);
        Assert.IsFalse(row.MoreCommands.OfType<ICommandContextItem>().Any(item => item.Command.Name == "Retry"));
        var actions = Assert.IsInstanceOfType<DetailsCommands>(row.Details!.Metadata.Single(fact => fact.Key == "Actions").Data);
        Assert.IsNotNull(actions.Commands);
        Assert.IsFalse(actions.Commands.Any(command => command.Name == "Retry"));
        Assert.IsTrue(unlocked, "Action notifications held the page lock.");
        Assert.AreSame(row, Rows(page).Single());
    }

    [TestMethod]
    public void StaleRetryDoesNotEnableADisabledSource()
    {
        var service = new StatusService(State() with { Status = MediaBackendLifecycleStatus.Faulted });
        var calls = 0;
        using var page = new MediaSourcesPage(service, (_, _) => calls++, new Dictionary<string, ICommand>(), NullLoggerFactory.Instance);
        var row = Rows(page).Single();
        var retry = Assert.IsInstanceOfType<IInvokableCommand>(row.MoreCommands.OfType<ICommandContextItem>().Single(item => item.Command.Name == "Retry").Command);
        service.Publish(State() with { IsEnabled = false, Status = MediaBackendLifecycleStatus.Disabled });

        Assert.AreEqual(CommandResultKind.KeepOpen, retry.Invoke(page).Kind);

        Assert.AreEqual(0, calls);
        Assert.AreEqual("Enable", row.Command!.Name);
        Assert.IsFalse(row.MoreCommands.OfType<ICommandContextItem>().Any(item => item.Command.Name == "Retry"));
    }

    [TestMethod]
    public void FailedRetryReportsFailureWithoutChangingTheChoice()
    {
        using var page = new MediaSourcesPage(new StatusService(State() with { Status = MediaBackendLifecycleStatus.Faulted }),
            (_, _) => throw new IOException(), new Dictionary<string, ICommand>(), NullLoggerFactory.Instance);
        var row = Rows(page).Single();
        var retry = Assert.IsInstanceOfType<IInvokableCommand>(row.MoreCommands.OfType<ICommandContextItem>().Single(item => item.Command.Name == "Retry").Command);

        var result = retry.Invoke(page);

        Assert.AreEqual(CommandResultKind.ShowToast, result.Kind);
        Assert.AreEqual(CommandResultKind.KeepOpen, Assert.IsInstanceOfType<ToastArgs>(result.Args).Result!.Kind);
        Assert.AreEqual("Enabled", Fact(row, "User setting"));
        Assert.AreEqual("Disable", row.Command!.Name);
    }

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
        Assert.AreEqual("Not connected", row.Tags.Single().Text);
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
    [DataRow(true, MediaBackendLifecycleStatus.Ready, MediaConnectionStatus.Connected, 0, "Ready", "Connected", "Waiting for playback")]
    [DataRow(true, MediaBackendLifecycleStatus.Ready, MediaConnectionStatus.Disconnected, 0, "Ready", "Disconnected", "Not connected")]
    [DataRow(true, MediaBackendLifecycleStatus.Starting, MediaConnectionStatus.Connecting, 0, "Starting", "Connecting", "Starting")]
    [DataRow(true, MediaBackendLifecycleStatus.Ready, MediaConnectionStatus.Unknown, 1, "Ready", "Unknown", "1 session")]
    [DataRow(true, MediaBackendLifecycleStatus.Faulted, MediaConnectionStatus.Unknown, 0, "Failed", "Unknown", "Needs attention")]
    [DataRow(false, MediaBackendLifecycleStatus.Stopping, MediaConnectionStatus.Disconnected, 0, "Stopping", "Disconnected", "Stopping")]
    [DataRow(false, MediaBackendLifecycleStatus.Disabled, MediaConnectionStatus.Disconnected, 0, "Disabled", "Disconnected", "Off")]
    public void StatusDimensionsRemainIndependent(bool enabled, MediaBackendLifecycleStatus lifecycle,
        MediaConnectionStatus connection, int count, string lifecycleText, string connectionText, string statusText)
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
        Assert.AreEqual(statusText, row.Tags.Single().Text);
        Assert.AreEqual(count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), Fact(row, "Available sessions"));
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
        Assert.AreEqual(1, items[0].MoreCommands.Length);
        Assert.IsEmpty(items[1].MoreCommands);
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
        Assert.AreEqual("Enable", row.Command!.Name);
    }

    [TestMethod]
    [DataRow("gsmtc", false)]
    [DataRow("gsmtc", true)]
    [DataRow("gsmtc.worker", false)]
    [DataRow("gsmtc.worker", true)]
    public void DisablingWindowsMediaRequiresConfirmation(string id, bool monitoring)
    {
        var state = State(id);
        var service = new StatusService(state, State("vlc"), State("itunes"));
        var changes = new List<(string, bool)>();
        using var page = new MediaSourcesPage(service, (changedId, enabled) =>
        {
            changes.Add((changedId, enabled));
            state = state with { IsEnabled = enabled, Status = MediaBackendLifecycleStatus.Disabled };
            service.Publish(state, State("vlc"), State("itunes"));
        }, new Dictionary<string, ICommand>(), NullLoggerFactory.Instance);
        if (monitoring) { page.ItemsChanged += (_, _) => { }; }
        var row = Rows(page).Single(item => item.Command!.Id.EndsWith($".{id}.enablement", StringComparison.Ordinal));
        var toggle = (IInvokableCommand)row.Command!;

        var result = toggle.Invoke(page);

        Assert.AreEqual(CommandResultKind.Confirm, result.Kind);
        var confirmation = Assert.IsInstanceOfType<IConfirmationArgs>(result.Args);
        Assert.AreEqual("Turn off Windows media?", confirmation.Title);
        Assert.Contains("Apple Music, Spotify, browser playback", confirmation.Description);
        Assert.Contains("cancel and enable the other Windows media source", confirmation.Description);
        Assert.IsTrue(confirmation.IsPrimaryCommandCritical);
        Assert.AreEqual("Turn off Windows media", confirmation.PrimaryCommand!.Name);
        Assert.IsNotNull(confirmation.PrimaryCommand.Icon);
        Assert.IsEmpty(changes);
        Assert.IsTrue(state.IsEnabled);

        var confirmed = Assert.IsInstanceOfType<IInvokableCommand>(confirmation.PrimaryCommand);
        Assert.AreEqual(CommandResultKind.KeepOpen, confirmed.Invoke(page).Kind);
        CollectionAssert.AreEqual(new[] { (id, false) }, changes);
        Assert.AreEqual("Enable", row.Command!.Name);
        Assert.IsFalse(state.IsEnabled);
        Assert.IsTrue(service.Backends.Single(backend => backend.Id == "vlc").IsEnabled);
        Assert.IsTrue(service.Backends.Single(backend => backend.Id == "itunes").IsEnabled);

        Assert.AreEqual(CommandResultKind.KeepOpen, confirmed.Invoke(page).Kind);
        Assert.HasCount(1, changes);
    }

    [TestMethod]
    [DataRow("gsmtc", "gsmtc.worker", false)]
    [DataRow("gsmtc", "gsmtc.worker", true)]
    [DataRow("gsmtc.worker", "gsmtc", false)]
    [DataRow("gsmtc.worker", "gsmtc", true)]
    public void StaleDisableConfirmationDoesNotTurnOffTheReplacement(string id, string replacementId, bool monitoring)
    {
        var state = State(id);
        var replacement = State(replacementId) with { IsEnabled = false };
        var service = new StatusService(state, replacement);
        var changes = new List<(string, bool)>();
        using var page = new MediaSourcesPage(service, (changedId, enabled) => changes.Add((changedId, enabled)),
            new Dictionary<string, ICommand>(), NullLoggerFactory.Instance);
        if (monitoring) { page.ItemsChanged += (_, _) => { }; }
        var row = Rows(page).Single(item => item.Command!.Id.EndsWith($".{id}.enablement", StringComparison.Ordinal));
        var result = ((IInvokableCommand)row.Command!).Invoke(page);
        Assert.AreEqual(CommandResultKind.Confirm, result.Kind);
        var confirmation = Assert.IsInstanceOfType<IConfirmationArgs>(result.Args);

        service.Publish(state with { IsEnabled = false }, replacement with { IsEnabled = true });
        var confirmed = Assert.IsInstanceOfType<IInvokableCommand>(confirmation.PrimaryCommand);

        Assert.AreEqual(CommandResultKind.KeepOpen, confirmed.Invoke(page).Kind);
        Assert.IsEmpty(changes);
        Assert.IsTrue(service.Backends.Single(backend => backend.Id == replacementId).IsEnabled);
    }

    [TestMethod]
    [DataRow("gsmtc", "gsmtc.worker")]
    [DataRow("gsmtc.worker", "gsmtc")]
    public void SwitchingWindowsMediaDoesNotAskForConfirmation(string id, string previousId)
    {
        var service = new StatusService(State(id) with { IsEnabled = false }, State(previousId));
        var changes = new List<(string, bool)>();
        using var page = new MediaSourcesPage(service, (changedId, enabled) => changes.Add((changedId, enabled)),
            new Dictionary<string, ICommand>(), NullLoggerFactory.Instance);
        var row = Rows(page).Single(item => item.Command!.Id.EndsWith($".{id}.enablement", StringComparison.Ordinal));

        Assert.AreEqual(CommandResultKind.KeepOpen, ((IInvokableCommand)row.Command!).Invoke(page).Kind);
        CollectionAssert.AreEqual(new[] { (id, true) }, changes);
    }

    [TestMethod]
    public void ConfirmedDisableFailurePreservesTheChoiceAndReportsFailure()
    {
        using var page = new MediaSourcesPage(new StatusService(State("gsmtc.worker")), (_, _) => throw new IOException(),
            new Dictionary<string, ICommand>(), NullLoggerFactory.Instance);
        var row = Rows(page).Single();
        var result = ((IInvokableCommand)row.Command!).Invoke(page);
        Assert.AreEqual(CommandResultKind.Confirm, result.Kind);
        var confirmation = Assert.IsInstanceOfType<IConfirmationArgs>(result.Args);
        var confirmed = Assert.IsInstanceOfType<IInvokableCommand>(confirmation.PrimaryCommand);

        var failure = confirmed.Invoke(page);

        Assert.AreEqual(CommandResultKind.ShowToast, failure.Kind);
        Assert.AreEqual("Disable", row.Command.Name);
        Assert.AreEqual("Enabled", Fact(row, "User setting"));
    }

    [TestMethod]
    [DataRow("vlc")]
    [DataRow("itunes")]
    public void OtherSourcesCanBeDisabledWithoutConfirmation(string id)
    {
        var changes = new List<(string, bool)>();
        using var page = new MediaSourcesPage(new StatusService(State(id)), (changedId, enabled) => changes.Add((changedId, enabled)),
            new Dictionary<string, ICommand>(), NullLoggerFactory.Instance);
        var command = (IInvokableCommand)Rows(page).Single().Command!;

        Assert.AreEqual(CommandResultKind.KeepOpen, command.Invoke(page).Kind);
        CollectionAssert.AreEqual(new[] { (id, false) }, changes);
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
        var first = State("gsmtc.worker") with { ExclusiveGroup = "windows" };
        var second = State("gsmtc") with { ExclusiveGroup = "windows", IsEnabled = false };
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
        Assert.AreEqual("Needs attention", row.Tags.Single().Text);
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
            foreach (var row in rows) { Assert.AreEqual("Needs attention", row.Tags.Single().Text); }
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
                foreach (var row in rows) { Assert.AreEqual("Needs attention", row.Tags.Single().Text); }
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
                Assert.AreEqual("Off", row.Tags.Single().Text);
                Assert.IsFalse(row.Details!.Metadata.Any(fact => fact.Data is DetailsLink link &&
                    link.Text.Contains("Could not read", StringComparison.Ordinal)));
                Assert.AreEqual("Enable", row.Command!.Name);
            }
            Assert.HasCount(3, changed.Where(static property => property == "Tags"));
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