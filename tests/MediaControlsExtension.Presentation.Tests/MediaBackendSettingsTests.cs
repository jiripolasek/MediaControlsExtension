// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Text.Json.Nodes;
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

    private static MediaBackendRegistry Registry() => new MediaBackendRegistry()
        .Register(new("gsmtc", "Windows media sessions", "", _ => throw new InvalidOperationException("Must not start during configuration."), true))
        .Register(new("vlc", "VLC", "", _ => throw new InvalidOperationException("Must not start during configuration.")));
}