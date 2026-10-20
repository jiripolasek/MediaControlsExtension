// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Text.Json.Nodes;
using JPSoftworks.MediaControlsExtension.Pages;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.MediaControlsExtension.Presentation.Tests;

[TestClass]
public sealed class MediaBackendConfigurationPageTests
{
    [TestMethod]
    public void PasswordStorageFailurePreventsEnablementAndCanBeCorrected()
    {
        var failPasswordSave = true;
        var storedPassword = "previous-secret";
        var vlc = new VlcSettings(_ => storedPassword, (_, password) =>
        {
            if (failPasswordSave) { throw new IOException(); }
            storedPassword = password;
        });
        var settings = new Settings();
        vlc.AddTo(settings);
        vlc.Apply();
        var enabled = false;
        var page = new MediaBackendConfigurationPage("vlc", "VLC", settings, vlc.Apply, NullLoggerFactory.Instance)
        {
            ValidateInputs = vlc.Validate,
            GetSaveError = vlc.GetSaveError,
            EnableSource = () => enabled = true,
            IsSourceEnabled = () => enabled,
        };
        var form = Assert.IsInstanceOfType<IFormContent>(page.GetContent().Single());
        var data = JsonNode.Parse(form.TemplateJson)!["actions"]![0]!["data"]!.ToJsonString();
        var inputs = new JsonObject
        {
            ["jpsoftworks.mediacontrols.MediaBackends.vlc.Endpoint"] = "http://127.0.0.1:8080",
            ["jpsoftworks.mediacontrols.MediaBackends.vlc.Password"] = "replacement-secret",
        }.ToJsonString();

        var failed = form.SubmitForm(inputs, data);

        Assert.IsFalse(enabled);
        Assert.AreEqual("previous-secret", vlc.GetOptions().Password);
        Assert.IsNotNull(vlc.GetSaveError());
        Assert.AreEqual(vlc.GetSaveError(), Assert.IsInstanceOfType<ToastArgs>(failed.Args).Message);
        Assert.DoesNotContain("replacement-secret", settings.ToJson());
        var refreshed = Assert.IsInstanceOfType<IFormContent>(page.GetContent().Single());
        Assert.Contains(vlc.GetSaveError()!, refreshed.TemplateJson);
        Assert.DoesNotContain("replacement-secret", refreshed.TemplateJson);
        failPasswordSave = false;

        refreshed.SubmitForm(inputs, data);

        Assert.IsTrue(enabled);
        Assert.AreEqual("replacement-secret", vlc.GetOptions().Password);
        Assert.IsNull(vlc.GetSaveError());
    }

    [TestMethod]
    public void SaveAndEnablePersistsBeforeEnablingAndRefreshesTheForm()
    {
        var settings = new Settings();
        var endpoint = new TextSetting("endpoint", "Server URL", "", "old");
        settings.Add(endpoint);
        var enabled = false;
        var saved = "";
        var page = new MediaBackendConfigurationPage("vlc", "VLC", settings, () => saved = endpoint.Value, NullLoggerFactory.Instance)
        {
            EnableSource = () => { Assert.AreEqual("new", saved); enabled = true; },
            IsSourceEnabled = () => enabled,
        };
        var notifications = 0;
        page.ItemsChanged += (_, _) => notifications++;
        var form = Assert.IsInstanceOfType<IFormContent>(page.GetContent().Single());
        var actions = JsonNode.Parse(form.TemplateJson)!["actions"]!.AsArray();
        Assert.AreEqual(2, actions.Count);
        Assert.AreEqual("Save", actions[1]!["title"]!.GetValue<string>());
        var data = actions[0]!["data"]!.ToJsonString();

        var result = form.SubmitForm("{\"endpoint\":\"new\"}", data);

        Assert.IsTrue(enabled);
        Assert.AreEqual(1, notifications);
        var toast = Assert.IsInstanceOfType<ToastArgs>(result.Args);
        Assert.AreEqual(CommandResultKind.KeepOpen, toast.Result!.Kind);
        Assert.Contains("enabled", toast.Message!);
        var refreshed = Assert.IsInstanceOfType<IFormContent>(page.GetContent().Single());
        Assert.AreEqual(1, JsonNode.Parse(refreshed.TemplateJson)!["actions"]!.AsArray().Count);
        Assert.Contains("new", refreshed.TemplateJson);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OrdinarySavePreservesTheEnabledChoice(bool enabled)
    {
        var settings = new Settings();
        var endpoint = new TextSetting("endpoint", "Server URL", "", "old");
        settings.Add(endpoint);
        var saved = false;
        var enableCalls = 0;
        var page = new MediaBackendConfigurationPage("vlc", "VLC", settings, () => saved = true, NullLoggerFactory.Instance)
        {
            EnableSource = () => enableCalls++,
            IsSourceEnabled = () => enabled,
        };
        var form = Assert.IsInstanceOfType<IFormContent>(page.GetContent().Single());
        var action = JsonNode.Parse(form.TemplateJson)!["actions"]!.AsArray().Single(node => node!["title"]!.GetValue<string>() == "Save")!;

        var result = form.SubmitForm("{\"endpoint\":\"new\",\"enableSource\":true}", action["data"]!.ToJsonString());

        Assert.IsTrue(saved);
        Assert.AreEqual("new", endpoint.Value);
        Assert.AreEqual(0, enableCalls);
        Assert.AreEqual(CommandResultKind.KeepOpen, Assert.IsInstanceOfType<ToastArgs>(result.Args).Result!.Kind);
    }

    [TestMethod]
    [DataRow("validation")]
    [DataRow("save")]
    public void FailedSetupDoesNotEnableTheSource(string failure)
    {
        var settings = new Settings();
        settings.Add(new TextSetting("endpoint", "Server URL", "", "old"));
        var enableCalls = 0;
        var saveCalls = 0;
        var page = new MediaBackendConfigurationPage("vlc", "VLC", settings,
            () => { saveCalls++; throw new IOException(); }, NullLoggerFactory.Instance)
        {
            ValidateInputs = _ => failure == "validation" ? "Invalid server URL" : null,
            EnableSource = () => enableCalls++,
        };
        var form = Assert.IsInstanceOfType<IFormContent>(page.GetContent().Single());
        var action = JsonNode.Parse(form.TemplateJson)!["actions"]![0]!;

        var result = form.SubmitForm("{\"endpoint\":\"new\"}", action["data"]!.ToJsonString());

        Assert.AreEqual(CommandResultKind.ShowToast, result.Kind);
        Assert.AreEqual(0, enableCalls);
        Assert.AreEqual(failure == "validation" ? 0 : 1, saveCalls);
        Assert.AreEqual(CommandResultKind.KeepOpen, Assert.IsInstanceOfType<ToastArgs>(result.Args).Result!.Kind);
    }

    [TestMethod]
    public void EnableFailureReportsThatSettingsWereSaved()
    {
        var settings = new Settings();
        settings.Add(new TextSetting("endpoint", "Server URL", "", "old"));
        var saved = false;
        var page = new MediaBackendConfigurationPage("vlc", "VLC", settings, () => saved = true, NullLoggerFactory.Instance)
        {
            EnableSource = () => throw new IOException(),
        };
        var notifications = 0;
        page.ItemsChanged += (_, _) => notifications++;
        var form = Assert.IsInstanceOfType<IFormContent>(page.GetContent().Single());
        var action = JsonNode.Parse(form.TemplateJson)!["actions"]![0]!;

        var result = form.SubmitForm("{\"endpoint\":\"new\"}", action["data"]!.ToJsonString());

        Assert.IsTrue(saved);
        Assert.AreEqual(1, notifications);
        var toast = Assert.IsInstanceOfType<ToastArgs>(result.Args);
        Assert.Contains("Settings were saved", toast.Message!);
        Assert.Contains("could not be enabled", toast.Message!);
        Assert.AreEqual(CommandResultKind.KeepOpen, toast.Result!.Kind);
    }
}