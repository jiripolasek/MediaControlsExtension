// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Text.Json.Nodes;
using System.Diagnostics;
using JPSoftworks.MediaControlsExtension.Media;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Pages;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.MediaControlsExtension.Presentation.Tests;

[TestClass]
public sealed class MediaBackendSettingsTests
{
    private const string VlcEnabled = "jpsoftworks.mediacontrols.MediaBackends.vlc.Enabled";
    private const string GsmtcEnabled = "jpsoftworks.mediacontrols.MediaBackends.gsmtc.Enabled";
    private const string VlcPort = "jpsoftworks.mediacontrols.MediaBackends.vlc.Port";
    private const string VlcPassword = "jpsoftworks.mediacontrols.MediaBackends.vlc.Password";
    private static readonly string[] RecoveredSources = ["gsmtc.worker", "vlc"];
    private string _directory = null!;
    private string _path = null!;

    [TestInitialize]
    public void Initialize()
    {
        this._directory = Directory.CreateTempSubdirectory("MediaControls-settings-tests-").FullName;
        this._path = Path.Combine(this._directory, "settings.json");
    }

    [TestCleanup]
    public void Cleanup()
    {
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(this._directory));
        Assert.StartsWith("MediaControls-settings-tests-", Path.GetFileName(this._directory));
        Directory.Delete(this._directory, recursive: true);
    }

    [TestMethod]
    public void ExistingKeysLoadAndEachFormPreservesOtherScopesAndUnknownSettings()
    {
        File.WriteAllText(this._path, new JsonObject
        {
            [VlcEnabled] = "true", [GsmtcEnabled] = "false", [VlcPort] = "8090", [VlcPassword] = null,
            ["ShowThumbnails"] = "false", ["future-provider"] = 17,
        }.ToJsonString());
        var store = this.Store();
        var backends = new MediaBackendSettings(Registry(), store);
        Assert.AreEqual("vlc", backends.EnabledIds.Single());
        var general = new Settings();
        var thumbnails = new ToggleSetting("ShowThumbnails", true);
        general.Add(thumbnails);
        store.Load(general);
        Assert.IsFalse(thumbnails.Value);
        var configuration = new Settings();
        var port = new TextSetting(VlcPort, "Port", "", "8080");
        configuration.Add(port);
        store.Load(configuration);
        Assert.AreEqual("8090", port.Value);

        port.Value = "8100";
        store.Save(configuration);
        Assert.AreEqual("vlc", backends.EnabledIds.Single());
        var notifications = 0;
        backends.EnabledChanged += (_, _) => notifications++;
        backends.SetEnabled("vlc", false);
        thumbnails.Value = true;
        store.Save(general);
        var saved = JsonNode.Parse(File.ReadAllText(this._path))!;
        Assert.AreEqual("8100", saved[VlcPort]!.GetValue<string>());
        Assert.AreEqual("false", saved[VlcEnabled]!.GetValue<string>());
        Assert.AreEqual("false", saved[GsmtcEnabled]!.GetValue<string>());
        Assert.AreEqual("true", saved["ShowThumbnails"]!.GetValue<string>());
        Assert.AreEqual(17, saved["future-provider"]!.GetValue<int>());
        Assert.IsNull(saved[VlcPassword]);
        Assert.AreEqual(1, notifications);
        Assert.IsEmpty(new MediaBackendSettings(Registry(), store).EnabledIds);
    }

    [TestMethod]
    public void FreshDefaultsDoNotWriteAFileAndFailedEnablementSaveRollsBack()
    {
        var settings = new MediaBackendSettings(Registry(), this.Store());
        Assert.AreEqual("gsmtc", settings.EnabledIds.Single());
        Assert.IsFalse(File.Exists(this._path));
        Directory.CreateDirectory(this._path + ".tmp");
        var notifications = 0;
        settings.EnabledChanged += (_, _) => notifications++;
        Assert.Throws<UnauthorizedAccessException>(() => settings.SetEnabled("vlc", true));
        Assert.AreEqual("gsmtc", settings.EnabledIds.Single());
        Assert.AreEqual(0, notifications);
    }

    [TestMethod]
    public void ExplicitEnablementIdentifiesOnlyTheSelectedRetryEvenWhenAlreadyEnabled()
    {
        var settings = new MediaBackendSettings(Registry(), this.Store());
        var choices = new List<(string?, bool)>();
        settings.EnabledChanged += (_, args) => choices.Add((args.BackendId, args.Enabled));

        settings.SetEnabled("gsmtc", true);
        settings.SetEnabled("vlc", true);
        settings.SetEnabled("vlc", false);

        CollectionAssert.AreEqual(new[] { ("gsmtc", true), ("vlc", true), ("vlc", false) }, choices);
    }

    [TestMethod]
    public void SettingsReadDistinguishesMissingMalformedUnreadableAndLoadedFiles()
    {
        var store = this.Store();
        Assert.AreEqual(SettingsReadStatus.Missing, store.ReadValues().Status);
        File.WriteAllText(this._path, "{\"duplicate\":1,\"duplicate\":2}");
        Assert.AreEqual(SettingsReadStatus.Malformed, store.ReadValues().Status);
        File.WriteAllText(this._path, "{\"unknown\":17}");
        using (var locked = new FileStream(this._path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var unreadable = store.ReadValues();
            Assert.AreEqual(SettingsReadStatus.Unreadable, unreadable.Status);
            Assert.IsNull(unreadable.Values);
            Assert.IsNotNull(unreadable.Error);
        }
        var loaded = store.ReadValues();
        Assert.AreEqual(SettingsReadStatus.Loaded, loaded.Status);
        Assert.AreEqual(17, loaded.Values!["unknown"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task ConcurrentFormSavesPreserveBothGroups()
    {
        var store = this.Store();
        var first = new Settings();
        first.Add(new TextSetting("general", "", "", "saved general"));
        var second = new Settings();
        second.Add(new TextSetting(VlcPort, "", "", "8100"));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saves = new[] { first, second }.Select(settings => Task.Run(async () =>
        {
            await start.Task;
            store.Save(settings);
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(saves);
        var saved = JsonNode.Parse(File.ReadAllText(this._path))!;
        Assert.AreEqual("saved general", saved["general"]!.GetValue<string>());
        Assert.AreEqual("8100", saved[VlcPort]!.GetValue<string>());
    }

    [TestMethod]
    public async Task LockedReadsAndTogglesReturnPromptlyWhileBackgroundRecoveryWaits()
    {
        File.WriteAllText(this._path, new JsonObject { [VlcEnabled] = "true", [GsmtcEnabled] = "false" }.ToJsonString());
        using var locked = new FileStream(this._path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var store = this.Store();
        var elapsed = Stopwatch.StartNew();
        using var settings = new MediaBackendSettings(Registry(), store);
        Assert.IsLessThan(TimeSpan.FromMilliseconds(250), elapsed.Elapsed);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        settings.EnabledChanged += (_, args) => { Assert.IsNull(args.BackendId); recovered.TrySetResult(); };
        await Task.Delay(150);
        elapsed.Restart();
        for (var index = 0; index < 8; index++) { Assert.AreEqual(SettingsReadStatus.Unreadable, store.ReadValues().Status); }
        Assert.Throws<IOException>(() => settings.SetEnabled("vlc", true));
        Assert.IsEmpty(settings.EnabledIds);
        Assert.IsNotNull(settings.GetDiagnostic("vlc"));
        Assert.IsLessThan(TimeSpan.FromMilliseconds(250), elapsed.Elapsed);
        locked.Dispose();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("vlc", settings.EnabledIds.Single());
        Assert.IsNull(settings.GetDiagnostic("gsmtc"));
        Assert.IsNull(settings.GetDiagnostic("vlc"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BackgroundRecoveryRestoresEverySavedSourceIndependentlyOfOtherForms(bool save)
    {
        File.WriteAllText(this._path, new JsonObject
        {
            [WorkerEnabled] = "true", [GsmtcEnabled] = "false", [VlcEnabled] = "true", ["unknown"] = 17,
        }.ToJsonString());
        var store = this.Store();
        MediaBackendSettings settings;
        using (var locked = new FileStream(this._path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            settings = new(ExclusiveRegistry(), store);
            Assert.IsEmpty(settings.EnabledIds);
        }
        using (settings)
        {
            var changes = new System.Collections.Concurrent.ConcurrentQueue<MediaBackendEnabledChangedEventArgs>();
            var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            settings.EnabledChanged += (_, args) => { changes.Enqueue(args); recovered.TrySetResult(); };
            if (save)
            {
                var general = new Settings();
                general.Add(new ToggleSetting("ShowThumbnails", false));
                store.Save(general);
            }
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            CollectionAssert.AreEquivalent(RecoveredSources, settings.EnabledIds.ToArray());
            foreach (var id in new[] { "gsmtc", "gsmtc.worker", "vlc" }) { Assert.IsNull(settings.GetDiagnostic(id)); }
            Assert.IsNull(changes.Single().BackendId);
            Assert.IsFalse(changes.Single().Enabled);
            store.ReadValues();
            Assert.HasCount(1, changes);
            Assert.AreEqual(17, JsonNode.Parse(File.ReadAllText(this._path))!["unknown"]!.GetValue<int>());
        }
    }

    [TestMethod]
    [DataRow(true, "gsmtc")]
    [DataRow(false, "gsmtc.worker")]
    public async Task ExplicitChoiceRecoversOtherSourcesAndPublishesOneFinalSelection(bool enabled, string selected)
    {
        File.WriteAllText(this._path, new JsonObject
        {
            [WorkerEnabled] = "true", [GsmtcEnabled] = "false", [VlcEnabled] = "true",
        }.ToJsonString());
        var store = this.Store();
        MediaBackendSettings settings;
        using (var locked = new FileStream(this._path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            settings = new(ExclusiveRegistry(), store);
        }
        using (settings)
        {
            var changes = new List<string[]>();
            settings.EnabledChanged += (_, args) =>
            {
                Assert.AreEqual("gsmtc", args.BackendId);
                Assert.AreEqual(enabled, args.Enabled);
                changes.Add(settings.EnabledIds.ToArray());
            };
            settings.SetEnabled("gsmtc", enabled);
            await Task.Delay(400);
            CollectionAssert.AreEquivalent(new[] { selected, "vlc" }, changes.Single());
            Assert.IsNull(settings.GetDiagnostic("vlc"));
            using var reloaded = new MediaBackendSettings(ExclusiveRegistry(), store);
            CollectionAssert.AreEquivalent(new[] { selected, "vlc" }, reloaded.EnabledIds.ToArray());
        }
    }

    [TestMethod]
    public void FailedExplicitSaveRestoresRecoveredSavedSelection()
    {
        File.WriteAllText(this._path, new JsonObject { [WorkerEnabled] = "true", [VlcEnabled] = "true" }.ToJsonString());
        var store = this.Store();
        MediaBackendSettings settings;
        using (var locked = new FileStream(this._path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            settings = new(ExclusiveRegistry(), store);
        }
        using (settings)
        {
            Directory.CreateDirectory(this._path + ".tmp");
            var changes = new List<MediaBackendEnabledChangedEventArgs>();
            settings.EnabledChanged += (_, args) => changes.Add(args);
            Assert.Throws<UnauthorizedAccessException>(() => settings.SetEnabled("gsmtc", true));
            CollectionAssert.AreEquivalent(RecoveredSources, settings.EnabledIds.ToArray());
            Assert.IsNull(changes.Single().BackendId);
            Assert.IsNull(settings.GetDiagnostic("vlc"));
        }
    }

    [TestMethod]
    public async Task DisposedSettingsDoNotRecoverAfterLaterReadsAndSaves()
    {
        File.WriteAllText(this._path, "{}");
        var store = this.Store();
        MediaBackendSettings settings;
        using (var locked = new FileStream(this._path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            settings = new(Registry(), store);
        }
        settings.Dispose();
        settings.EnabledChanged += (_, _) => Assert.Fail("Disposed settings received a recovery notification.");
        store.Save(new Settings());
        store.ReadValues();
        await Task.Delay(300);
        Assert.IsEmpty(settings.EnabledIds);
    }

    [TestMethod]
    public async Task RecoverySubscriberFailureCannotFailAnUnrelatedSave()
    {
        File.WriteAllText(this._path, new JsonObject { [VlcEnabled] = "true" }.ToJsonString());
        var store = this.Store();
        MediaBackendSettings settings;
        using (var locked = new FileStream(this._path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            settings = new(Registry(), store);
        }
        using (settings)
        {
            var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            settings.EnabledChanged += (_, _) =>
            {
                notified.TrySetResult();
                throw new InvalidOperationException("Injected recovery callback failure.");
            };
            var form = new Settings();
            form.Add(new TextSetting(VlcPort, "", "", "8100"));
            store.Save(form);
            await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
            store.Save(form);
            Assert.AreEqual("8100", JsonNode.Parse(File.ReadAllText(this._path))![VlcPort]!.GetValue<string>());
            Assert.IsTrue(settings.EnabledIds.Contains("vlc"));
        }
    }

    [TestMethod]
    public void ConfigurationSaveKeepsThePageOpenAndDoesNotEnableTheProviderOrExposePasswords()
    {
        var store = this.Store();
        var backends = new MediaBackendSettings(Registry(), store);
        var settings = new Settings();
        var port = new TextSetting(VlcPort, "Port", "", "8080");
        var savedPassword = "existing-secret";
        settings.Add(port);
        settings.Add(new VlcPasswordSetting(VlcPassword, "Password", "", "", () => savedPassword, value => savedPassword = value));
        var appliedPort = "8080";
        var page = new MediaBackendConfigurationPage("vlc", "VLC", settings,
            () => { store.Save(settings); appliedPort = port.Value; }, NullLoggerFactory.Instance);
        var notifications = 0;
        page.ItemsChanged += (_, _) => notifications++;
        var form = (IFormContent)page.GetContent().Single();
        Assert.DoesNotContain("existing-secret", form.TemplateJson);
        var input = new JsonObject { [VlcPort] = "8090", [VlcPassword] = "new-secret", [VlcEnabled] = "true" };
        Assert.AreEqual(CommandResultKind.KeepOpen, form.SubmitForm(input.ToJsonString(), "{}").Kind);
        Assert.AreEqual("8090", appliedPort);
        Assert.AreEqual("new-secret", savedPassword);
        Assert.AreEqual("gsmtc", backends.EnabledIds.Single());
        var saved = File.ReadAllText(this._path);
        Assert.DoesNotContain("new-secret", saved);
        Assert.IsNull(JsonNode.Parse(saved)![VlcEnabled]);
        Assert.IsNull(JsonNode.Parse(saved)![VlcPassword]);
        Assert.AreEqual(1, notifications);
        var refreshed = (IFormContent)page.GetContent().Single();
        Assert.Contains("8090", refreshed.TemplateJson);
        Assert.DoesNotContain("new-secret", refreshed.TemplateJson);
        refreshed.SubmitForm(new JsonObject { [VlcPort] = "8091", [VlcPassword] = "" }.ToJsonString(), "{}");
        Assert.AreEqual("new-secret", savedPassword);
    }

    [TestMethod]
    public void FailedConfigurationSaveStaysOpenAndDoesNotApplyOptions()
    {
        var settings = new Settings();
        settings.Add(new TextSetting(VlcPort, "Port", "", "8080"));
        var page = new MediaBackendConfigurationPage("vlc", "VLC", settings,
            () => throw new IOException(), NullLoggerFactory.Instance);
        var form = (IFormContent)page.GetContent().Single();
        Assert.AreEqual(CommandResultKind.ShowToast,
            form.SubmitForm(new JsonObject { [VlcPort] = "8090" }.ToJsonString(), "{}").Kind);
        Assert.IsFalse(File.Exists(this._path));
    }

    private SettingsStore Store() => new(this._path, NullLogger.Instance);

    [TestMethod]
    public void WorkerSelectionSavesBothFlagsOnceAndPreservesLegacyDisablement()
    {
        var settings = new MediaBackendSettings(ExclusiveRegistry(), this.Store());
        Assert.AreEqual("gsmtc", settings.EnabledIds.Single());
        var notifications = 0;
        settings.EnabledChanged += (_, _) => notifications++;
        settings.SetEnabled("gsmtc.worker", true);
        Assert.AreEqual(1, notifications);
        Assert.AreEqual("gsmtc.worker", settings.EnabledIds.Single());
        var saved = JsonNode.Parse(File.ReadAllText(this._path))!;
        Assert.AreEqual("false", saved[GsmtcEnabled]!.GetValue<string>());
        Assert.AreEqual("true", saved[WorkerEnabled]!.GetValue<string>());
        Assert.AreEqual("gsmtc.worker", new MediaBackendSettings(ExclusiveRegistry(), this.Store()).EnabledIds.Single());
        settings.SetEnabled("gsmtc.worker", false);
        Assert.IsEmpty(new MediaBackendSettings(ExclusiveRegistry(), this.Store()).EnabledIds);
    }

    [TestMethod]
    public void FailedSwitchRestoresEveryGroupFlagAndPublishesNoChange()
    {
        var settings = new MediaBackendSettings(ExclusiveRegistry(), this.Store());
        Directory.CreateDirectory(this._path + ".tmp");
        var notifications = 0;
        settings.EnabledChanged += (_, _) => notifications++;
        Assert.Throws<UnauthorizedAccessException>(() => settings.SetEnabled("gsmtc.worker", true));
        Assert.AreEqual("gsmtc", settings.EnabledIds.Single());
        Assert.AreEqual(0, notifications);
    }

    [TestMethod]
    public void CorruptGroupStaysInactiveUntilExplicitlyRepaired()
    {
        File.WriteAllText(this._path, new JsonObject
        {
            [GsmtcEnabled] = "true", [WorkerEnabled] = "true", [VlcEnabled] = "true",
        }.ToJsonString());
        var settings = new MediaBackendSettings(ExclusiveRegistry(), this.Store());
        Assert.AreEqual("vlc", settings.EnabledIds.Single());
        Assert.IsNotNull(settings.GetDiagnostic("gsmtc.worker"));
        settings.SetEnabled("vlc", false);
        var unchangedGroup = JsonNode.Parse(File.ReadAllText(this._path))!;
        Assert.AreEqual("true", unchangedGroup[GsmtcEnabled]!.GetValue<string>());
        Assert.AreEqual("true", unchangedGroup[WorkerEnabled]!.GetValue<string>());
        settings.SetEnabled("gsmtc.worker", true);
        Assert.AreEqual("gsmtc.worker", settings.EnabledIds.Single());
        Assert.IsNull(settings.GetDiagnostic("gsmtc"));
        Assert.IsNull(settings.GetDiagnostic("gsmtc.worker"));
    }

    [TestMethod]
    public void ExplicitWorkerChoiceSuppressesAbsentInternalDefault()
    {
        File.WriteAllText(this._path, new JsonObject { [WorkerEnabled] = "true" }.ToJsonString());
        var settings = new MediaBackendSettings(ExclusiveRegistry(), this.Store());
        Assert.AreEqual("gsmtc.worker", settings.EnabledIds.Single());
        Assert.IsNull(settings.GetDiagnostic("gsmtc.worker"));
        File.WriteAllText(this._path, new JsonObject { [GsmtcEnabled] = "false" }.ToJsonString());
        Assert.IsEmpty(new MediaBackendSettings(ExclusiveRegistry(), this.Store()).EnabledIds);
    }

    [TestMethod]
    public void SettingsBindingRequiresAPageForwardsChoicesAndStopsAfterDisposal()
    {
        var registry = Registry();
        using var settings = new MediaBackendSettings(registry, this.Store());
        using var service = new MediaService(new CompositeMediaBackend(registry, settings.EnabledIds));
        using var page = new MediaSourcesPage(service, settings.SetEnabled, new Dictionary<string, ICommand>(),
            NullLoggerFactory.Instance, settings.GetDiagnostic);
        var retries = new List<string?>();
        Assert.Throws<ArgumentNullException>(() => new MediaBackendSettingsBinding(settings, null!, retries.Add));
        using var binding = new MediaBackendSettingsBinding(settings, page, retries.Add);
        Assert.HasCount(1, retries);
        Assert.IsNull(retries[0]);

        settings.SetEnabled("vlc", true);
        Assert.AreEqual("vlc", retries[1]);
        settings.SetEnabled("vlc", false);
        Assert.IsNull(retries[2]);
        binding.Dispose();
        settings.SetEnabled("vlc", true);
        Assert.HasCount(3, retries);
    }

    private const string WorkerEnabled = "jpsoftworks.mediacontrols.MediaBackends.gsmtc.worker.Enabled";

    private static MediaBackendRegistry ExclusiveRegistry() => new MediaBackendRegistry()
        .Register(new("gsmtc", "Internal", "", _ => throw new InvalidOperationException(), true) { ExclusiveGroup = "windows" })
        .Register(new("gsmtc.worker", "Worker", "", _ => throw new InvalidOperationException()) { ExclusiveGroup = "windows" })
        .Register(new("vlc", "VLC", "", _ => throw new InvalidOperationException()));

    private static MediaBackendRegistry Registry() => new MediaBackendRegistry()
        .Register(new("gsmtc", "Windows media sessions", "", _ => throw new InvalidOperationException("Must not start during configuration."), true))
        .Register(new("vlc", "VLC", "", _ => throw new InvalidOperationException("Must not start during configuration.")));
}