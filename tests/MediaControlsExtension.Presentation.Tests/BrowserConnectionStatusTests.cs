// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using JPSoftworks.MediaControlsExtension.BrowserIntegration;
using JPSoftworks.MediaControlsExtension.Pages;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.MediaControlsExtension.Presentation.Tests;

[TestClass]
public sealed class BrowserConnectionStatusTests
{
    private static readonly string[] ActiveBrowserNames = ["Firefox", "Edge"];
    private static readonly string[] InactiveBrowserNames = ["Brave", "Chrome"];

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SetupActionsStayBeforeLargeBrowserLists(bool enabled)
    {
        var installed = Enumerable.Range(1, 20).Select(index => new InstalledBrowser($"Browser {index}", $@"C:\Browser{index}\browser.exe")).ToArray();
        using var page = new BrowserCompanionPage(_ => { }, () => enabled, NullLoggerFactory.Instance,
            getRegistration: () => (string.Empty, enabled), getBrowsers: () => installed);
        var card = JsonNode.Parse(Assert.IsInstanceOfType<IFormContent>(page.GetContent().Single()).TemplateJson)!;
        var body = card["body"]!.AsArray();
        var actions = body.Single(item => item?["id"]?.GetValue<string>() == "setupActions")!;
        var connections = body.Single(item => item?["id"]?.GetValue<string>() == "browserConnections")!;
        Assert.IsTrue(body.IndexOf(actions) < body.IndexOf(connections));
        Assert.IsTrue(body.IndexOf(actions) <= 3);
        var buttons = actions["actions"]!.AsArray();
        var guide = buttons.Single(action => action?["id"]?.GetValue<string>() == "getExtension")!;
        Assert.AreEqual("Action.OpenUrl", guide["type"]!.GetValue<string>());
        Assert.AreEqual("https://mediacontrols.jiripolasek.com/companions/", guide["url"]!.GetValue<string>());
        var advanced = buttons.Single(action => action?["id"]?.GetValue<string>() == "advancedSetup")!;
        Assert.AreEqual("Action.ShowCard", advanced["type"]!.GetValue<string>());
        Assert.IsTrue(Descendants(advanced).Any(node => node is JsonObject && node["id"]?.GetValue<string>() == "extensionIds"));
        Assert.AreEqual(!enabled, buttons.Any(action => action?["id"]?.GetValue<string>() == "enableIntegration"));
    }

    [TestMethod]
    public void CompactBrowserRowsKeepActiveConnectionsVisibleAndInactiveBrowsersCollapsed()
    {
        var service = new StatusService(Windows(), Companion(
            Connection("firefox", "Firefox", @"C:\Firefox\firefox.exe", MediaConnectionSource.Provider),
            Connection("edge", "Edge", @"C:\Edge\browser.exe", MediaConnectionSource.Reconnecting, MediaConnectionStatus.Connecting)));
        using var page = Page(service, [new("Brave", @"C:\Brave\brave.exe"), new("Chrome", @"C:\Chrome\chrome.exe")]);
        var card = JsonNode.Parse(Assert.IsInstanceOfType<IFormContent>(page.GetContent().Single()).TemplateJson)!;
        var connections = card["body"]!.AsArray().Single(item => item?["id"]?.GetValue<string>() == "browserConnections")!;
        var items = connections["items"]!.AsArray();
        var visibleRows = items.Where(IsBrowserRow).ToArray();
        Assert.HasCount(2, visibleRows);
        CollectionAssert.AreEquivalent(ActiveBrowserNames, visibleRows.Select(row => Cell(row!, 0)).ToArray());
        foreach (var row in Rows(page))
        {
            Assert.AreEqual("ColumnSet", row["items"]![0]!["type"]!.GetValue<string>());
            Assert.AreEqual(3, row["items"]![0]!["columns"]!.AsArray().Count);
            Assert.HasCount(1, row["items"]!.AsArray());
        }
        var other = items.Single(item => item?["id"]?.GetValue<string>() == "otherBrowsers")!["actions"]![0]!;
        Assert.AreEqual("Action.ShowCard", other["type"]!.GetValue<string>());
        var hiddenRows = other["card"]!["body"]!.AsArray().Where(IsBrowserRow).ToArray();
        Assert.HasCount(2, hiddenRows);
        CollectionAssert.AreEquivalent(InactiveBrowserNames, hiddenRows.Select(row => Cell(row!, 0)).ToArray());
        Assert.AreEqual(BrowserCompanionPage.Text("OtherBrowsers").Replace("{0}", "2", StringComparison.Ordinal),
            other["title"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("gsmtc", "Settings_Backend_Gsmtc_Title")]
    [DataRow("gsmtc.worker", "Settings_Backend_GsmtcWorker_Title")]
    public void FallbackDisplaysTheSelectedWindowsBackend(string selectedId, string titleKey)
    {
        var selected = new MediaBackendState(selectedId, true, MediaBackendLifecycleStatus.Ready, null)
        {
            DisplayName = Label(titleKey)
        };
        var unselected = new MediaBackendState(selectedId == "gsmtc" ? "gsmtc.worker" : "gsmtc", false,
            MediaBackendLifecycleStatus.Disabled, null);
        var service = new StatusService(unselected, selected, Companion(
            Connection("edge", "Edge", @"C:\Edge\browser.exe", MediaConnectionSource.Provider)) with { IsEnabled = false });
        using var page = Page(service, [new("Edge", @"C:\Edge\browser.exe")]);
        Assert.AreEqual(Label(titleKey), Source(Row(page, "Edge")));
        Assert.AreEqual(MediaSourcesPage.Text("Connection_Disconnected"), Status(Row(page, "Edge")));
    }

    [TestMethod]
    public void SharedPageShowsMixedBrowserSourcesIncludingBrave()
    {
        var service = new StatusService(Windows(), Companion(
            Connection("edge", "Microsoft Edge", @"C:\Edge\browser.exe", MediaConnectionSource.Provider),
            Connection("brave", "Brave", @"C:\Brave\brave.exe", MediaConnectionSource.Fallback)));
        using var page = Page(service, [new("Microsoft Edge", @"C:\Edge\browser.exe"), new("Brave", @"C:\Brave\brave.exe"),
            new("Mozilla Firefox", @"C:\Firefox\firefox.exe")]);
        Assert.HasCount(3, Rows(page));
        Assert.AreEqual(Label("Settings_Backend_Browser_Title"), Source(Row(page, "Microsoft Edge")));
        Assert.AreEqual(Label("Settings_Backend_GsmtcWorker_Title"), Source(Row(page, "Brave")));
        Assert.AreEqual(Label("Settings_Backend_GsmtcWorker_Title"), Source(Row(page, "Mozilla Firefox")));
        Assert.AreEqual(MediaSourcesPage.Text("Connection_Connected"), Status(Row(page, "Brave")));
        Assert.AreEqual(MediaSourcesPage.Text("Connection_Disconnected"), Status(Row(page, "Mozilla Firefox")));
        Assert.HasCount(0, page.Commands);
    }

    [TestMethod]
    public void BrowserProfilesAreGroupedWithoutCombiningChannelsOrUnknownBrands()
    {
        var service = new StatusService(Windows(), Companion(
            Connection("one", "Edge", @"C:\Edge\browser.exe", MediaConnectionSource.Provider),
            Connection("two", "Edge", @"c:\edge\BROWSER.exe", MediaConnectionSource.Reconnecting, MediaConnectionStatus.Connecting),
            Connection("dev", "Edge Dev", @"C:\EdgeDev\browser.exe", MediaConnectionSource.Provider),
            Connection("custom", "Custom Browser", @"C:\Custom\browser.exe", MediaConnectionSource.Fallback)));
        using var page = Page(service, [new("Installed Edge", @"C:\Edge\browser.exe")]);
        Assert.HasCount(3, Rows(page));
        Assert.AreEqual(Label("Settings_Backend_Browser_Title"), Source(Row(page, "Installed Edge")));
        Assert.AreEqual(MediaSourcesPage.Text("Connection_Connected"), Status(Row(page, "Installed Edge")));
        Assert.AreEqual(Label("Settings_Backend_Browser_Title"), Source(Row(page, "Edge Dev")));
        Assert.AreEqual(Label("Settings_Backend_GsmtcWorker_Title"), Source(Row(page, "Custom Browser")));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RecoveryGraceDoesNotClaimWindowsMediaIsAlreadyInUse(bool windowsEnabled)
    {
        var service = new StatusService(Windows() with { IsEnabled = windowsEnabled }, Companion(
            Connection("edge", "Edge", @"C:\Edge\browser.exe", MediaConnectionSource.Reconnecting, MediaConnectionStatus.Connecting)));
        using var page = Page(service);
        Assert.AreEqual(BrowserCompanionPage.Text("Waiting"), Source(Row(page, "Edge")));
        Assert.AreEqual(MediaSourcesPage.Text("Connection_Connecting"), Status(Row(page, "Edge")));
    }

    [TestMethod]
    [DataRow(false, MediaBackendLifecycleStatus.Disabled, "Disabled")]
    [DataRow(true, MediaBackendLifecycleStatus.Starting, "Lifecycle_Starting")]
    [DataRow(true, MediaBackendLifecycleStatus.Faulted, "Lifecycle_Faulted")]
    public void WindowsFallbackReportsDisabledStartingAndFaultedProviders(bool enabled, MediaBackendLifecycleStatus state, string status)
    {
        var service = new StatusService(Windows() with { IsEnabled = enabled, Status = state });
        using var page = Page(service, [new("Brave", @"C:\Brave\brave.exe")]);
        Assert.AreEqual($"{Label("Settings_Backend_GsmtcWorker_Title")} ({MediaSourcesPage.Text(status)})", Source(Row(page, "Brave")));
    }

    [TestMethod]
    public void DisabledCompanionIgnoresCachedOwnership()
    {
        var service = new StatusService(Windows(), Companion(
            Connection("brave", "Brave", @"C:\Brave\brave.exe", MediaConnectionSource.Provider)) with { IsEnabled = false });
        using var page = Page(service, [new("Brave", @"C:\Brave\brave.exe")]);
        Assert.AreEqual(Label("Settings_Backend_GsmtcWorker_Title"), Source(Row(page, "Brave")));
        Assert.AreEqual(MediaSourcesPage.Text("Connection_Disconnected"), Status(Row(page, "Brave")));
    }

    [TestMethod]
    public void DisabledWindowsDoesNotDisplayAnActiveFallbackDiagnostic()
    {
        const string diagnostic = "Using Windows media sessions - fixture";
        var service = new StatusService(Windows() with { IsEnabled = false }, Companion(
            Connection("brave", "Brave", @"C:\Brave\brave.exe", MediaConnectionSource.Fallback) with { DiagnosticMessage = diagnostic }));
        using var page = Page(service);
        var form = Assert.IsInstanceOfType<IFormContent>(page.GetContent().Single());
        Assert.IsFalse(form.TemplateJson.Contains(diagnostic, StringComparison.Ordinal));
        Assert.AreEqual($"{Label("Settings_Backend_GsmtcWorker_Title")} ({MediaSourcesPage.Text("Disabled")})", Source(Row(page, "Brave")));
    }

    [TestMethod]
    [DataRow(MediaBackendLifecycleStatus.Starting)]
    [DataRow(MediaBackendLifecycleStatus.Stopping)]
    [DataRow(MediaBackendLifecycleStatus.Faulted)]
    public void InactiveBackendCannotReportCachedCompanionOwnership(MediaBackendLifecycleStatus lifecycle)
    {
        var service = new StatusService(Windows(), Companion(
            Connection("brave", "Brave", @"C:\Brave\brave.exe", MediaConnectionSource.Provider)) with { Status = lifecycle });
        using var page = Page(service);
        Assert.AreEqual(MediaSourcesPage.Text("Connection_Unknown"), Source(Row(page, "Brave")));
        Assert.AreEqual(MediaSourcesPage.Text("Connection_Unknown"), Status(Row(page, "Brave")));
    }

    [TestMethod]
    [DataRow(MediaConnectionSource.Provider)]
    [DataRow(MediaConnectionSource.Reconnecting)]
    public void FailedObservationDoesNotReportCompanionOrWindowsOwnership(MediaConnectionSource previousSource)
    {
        var service = new StatusService(Windows(), Companion(
            Connection("brave", "Brave", @"C:\Brave\brave.exe", previousSource, MediaConnectionStatus.Unknown)));
        using var page = Page(service);
        Assert.AreEqual(MediaSourcesPage.Text("Connection_Unknown"), Source(Row(page, "Brave")));
        Assert.AreEqual(MediaSourcesPage.Text("Connection_Unknown"), Status(Row(page, "Brave")));
    }

    [TestMethod]
    public void BackendChangesRefreshStatusesAndDisposalReleasesTheSubscription()
    {
        var service = new StatusService(Windows());
        using var page = Page(service, [new("Brave", @"C:\Brave\brave.exe")]);
        var changes = 0;
        page.ItemsChanged += (_, _) => changes++;
        service.Publish(Windows(), Companion(Connection("brave", "Brave", @"C:\Brave\brave.exe", MediaConnectionSource.Provider)));
        Assert.AreEqual(1, changes);
        Assert.AreEqual(Label("Settings_Backend_Browser_Title"), Source(Row(page, "Brave")));
        Assert.AreEqual(1, service.SubscriberCount);
        page.Dispose();
        page.Dispose();
        page.Observe(service);
        service.Publish(Windows());
        Assert.AreEqual(1, changes);
        Assert.AreEqual(0, service.SubscriberCount);
        Assert.AreEqual(0, service.DisposeCount);
    }

    [TestMethod]
    public void EmptyDiscoveryShowsAnExplicitEmptyState()
    {
        using var page = Page(new StatusService(Windows()));
        var form = Assert.IsInstanceOfType<IFormContent>(page.GetContent().Single());
        Assert.HasCount(0, Rows(page));
        Assert.Contains(BrowserCompanionPage.Text("NoBrowsers"), form.TemplateJson);
    }

    [TestMethod]
    [DataRow("Brave", "\"C:\\Program Files\\Brave\\brave.exe\" --flag", "C:\\Program Files\\Brave\\brave.exe")]
    [DataRow("Custom Browser", "C:\\Custom Browser\\app.EXE --flag", "C:\\Custom Browser\\app.EXE")]
    public void InstalledBrowserDiscoveryAcceptsAnyBrand(string name, string command, string expectedPath)
    {
        var browser = InstalledBrowsers.FromRegistration(name, command);
        Assert.IsNotNull(browser);
        Assert.AreEqual(name, browser.DisplayName);
        Assert.AreEqual(expectedPath, browser.ExecutablePath);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("browser.exe")]
    [DataRow("\"C:\\Browser\\browser.exe")]
    [DataRow("C:\\Browser\\browser.exe.cmd")]
    public void InstalledBrowserDiscoveryRejectsMalformedCommands(string? command) =>
        Assert.IsNull(InstalledBrowsers.FromRegistration("Browser", command));

    [TestMethod]
    [DataRow(null)]
    [DataRow(" ")]
    [DataRow("@browser.exe,-1")]
    public void MissingOrIndirectBrowserNamesUseTheExecutableName(string? name)
    {
        var browser = InstalledBrowsers.FromRegistration(name, @"C:\Browser\custom.exe");
        Assert.IsNotNull(browser);
        Assert.AreEqual("custom", browser.DisplayName);
        Assert.AreEqual(@"C:\Browser\custom.exe", browser.ExecutablePath);
    }

    private static string Label(string key) => Strings.ResourceManager.GetString(key, Strings.Culture)!;
    private static MediaBackendState Windows() => new("gsmtc.worker", true, MediaBackendLifecycleStatus.Ready, null)
    {
        DisplayName = Label("Settings_Backend_GsmtcWorker_Title")
    };
    private static MediaBackendState Companion(params MediaConnectionState[] connections) =>
        new("browser.companion", true, MediaBackendLifecycleStatus.Ready, null)
        {
            Connection = new(MediaConnectionStatus.Connected) { Connections = [.. connections] }
        };
    private static MediaConnectionState Connection(string id, string name, string path, MediaConnectionSource source,
        MediaConnectionStatus status = MediaConnectionStatus.Connected) => new(id, name + " profile", status)
        {
            ApplicationName = name, ApplicationPath = path, Source = source
        };
    private static BrowserCompanionPage Page(StatusService service, InstalledBrowser[]? installed = null)
    {
        var page = new BrowserCompanionPage(_ => { }, () => true, NullLoggerFactory.Instance,
            getRegistration: () => (string.Empty, true), getBrowsers: () => installed ?? []);
        page.Observe(service);
        return page;
    }
    private static JsonNode[] Rows(BrowserCompanionPage page)
    {
        var form = Assert.IsInstanceOfType<IFormContent>(page.GetContent().Single());
        return [.. Descendants(JsonNode.Parse(form.TemplateJson)!).Where(IsBrowserRow)];
    }
    private static bool IsBrowserRow(JsonNode? node) => node is JsonObject && node["id"]?.GetValue<string>()?.StartsWith("browserRow-", StringComparison.Ordinal) == true;
    private static JsonNode Row(BrowserCompanionPage page, string name) => Rows(page).Single(row => Cell(row, 0) == name);
    private static string Cell(JsonNode row, int index) => row["items"]![0]!["columns"]![index]!["items"]![0]!["text"]!.GetValue<string>();
    private static string Source(JsonNode row) => Cell(row, 2);
    private static string Status(JsonNode row) => Cell(row, 1);
    private static IEnumerable<JsonNode> Descendants(JsonNode node)
    {
        yield return node;
        var children = node switch
        {
            JsonObject obj => obj.Select(property => property.Value),
            JsonArray array => array.AsEnumerable(),
            _ => []
        };
        foreach (var child in children)
            if (child is not null)
                foreach (var descendant in Descendants(child)) yield return descendant;
    }

    private sealed class StatusService(params MediaBackendState[] states) : IMediaService
    {
        private EventHandler? _changed;
        public int SubscriberCount => this._changed?.GetInvocationList().Length ?? 0;
        public int DisposeCount { get; private set; }
        public event EventHandler? BackendsChanged { add => this._changed += value; remove => this._changed -= value; }
        public ImmutableArray<MediaBackendState> Backends { get; private set; } = [.. states];
        public void Publish(params MediaBackendState[] next) { this.Backends = [.. next]; this._changed?.Invoke(this, EventArgs.Empty); }
        public event EventHandler? SessionsChanged { add { } remove { } }
        public event EventHandler? CurrentSessionChanged { add { } remove { } }
        public event EventHandler? StatusChanged { add { } remove { } }
        public ImmutableArray<MediaSession> Sessions => [];
        public MediaSession? CurrentSession => null;
        public MediaServiceStatus Status => MediaServiceStatus.Ready;
        public MediaControlAvailability Availability => MediaControlAvailability.Available;
        public Task StartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public MediaCommandSubmission TrySubmit(MediaCommand command) => throw new NotSupportedException();
        public ValueTask<MediaArtworkContent?> GetArtworkAsync(MediaArtworkKey key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void UpdateOptions(MediaServiceOptions options) => throw new NotSupportedException();
        public void Dispose() => this.DisposeCount++;
        public ValueTask DisposeAsync() { this.Dispose(); return ValueTask.CompletedTask; }
    }
}