// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Text.Json.Nodes;
using JPSoftworks.MediaControlsExtension.Helpers;
using JPSoftworks.MediaControlsExtension.Pages;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JPSoftworks.MediaControlsExtension.Presentation.Tests;

[TestClass]
public sealed class VlcSettingsTests
{
    private const string Prefix = "jpsoftworks.mediacontrols.MediaBackends.vlc.";

    [TestMethod]
    public void LegacyPortAndPasswordMigrateOnlyToTheOriginalLocalEndpoint()
    {
        var vault = new Dictionary<string, string> { ["http"] = "original-secret" };
        var configuration = new VlcSettings(key => vault.GetValueOrDefault(key, ""), (key, password) => vault[key] = password);
        var settings = new Settings();
        configuration.AddTo(settings);
        settings.Update(new JsonObject { [Prefix + "Port"] = "8090", [Prefix + "TreatAsLocal"] = false }.ToJsonString());
        configuration.Apply();
        var options = configuration.GetOptions();
        Assert.AreEqual("http://127.0.0.1:8090/", options.Endpoint);
        Assert.AreEqual("original-secret", options.Password);
        Assert.IsFalse(options.TreatAsLocal);
        Assert.IsTrue(options.IsLocalConnection);
        Assert.AreEqual("original-secret", vault[options.Endpoint!]);
        Assert.IsFalse(vault.ContainsKey("http://127.0.0.1:8080/"));
        Assert.DoesNotContain("original-secret", settings.ToJson());
        Assert.IsNull(JsonNode.Parse(settings.ToJson())![Prefix + "Password"]);

        settings.Update(Payload("http://other-pc:8090"));
        configuration.Apply();
        Assert.AreEqual(string.Empty, configuration.GetOptions().Password);
        Assert.IsFalse(configuration.GetOptions().IsLocalConnection);
        Assert.IsFalse(vault.ContainsKey("http://other-pc:8090/"));
    }

    [TestMethod]
    public void PasswordsStayWithTheirEndpointAcrossChangesAndSettingsReload()
    {
        var vault = new Dictionary<string, string>();
        var configuration = new VlcSettings(key => vault.GetValueOrDefault(key, ""), (key, password) => vault[key] = password);
        var settings = new Settings();
        configuration.AddTo(settings);
        settings.Update(Payload("http://first-pc:8080", "first-secret"));
        configuration.Apply();
        settings.Update(Payload("https://second-pc:8443", "second-secret"));
        configuration.Apply();
        Assert.AreEqual("second-secret", configuration.GetOptions().Password);
        settings.Update(Payload("HTTP://FIRST-PC:8080/"));
        configuration.Apply();
        Assert.AreEqual("first-secret", configuration.GetOptions().Password);
        Assert.AreEqual(2, vault.Count);

        var reloaded = new VlcSettings(key => vault.GetValueOrDefault(key, ""), (key, password) => vault[key] = password);
        var reloadedSettings = new Settings();
        reloaded.AddTo(reloadedSettings);
        reloadedSettings.Update(settings.ToJson());
        reloaded.Apply();
        Assert.AreEqual(configuration.GetOptions(), reloaded.GetOptions());
        var form = ((IFormContent)reloadedSettings.ToContent()[0]).TemplateJson;
        Assert.DoesNotContain("first-secret", form);
        Assert.DoesNotContain("second-secret", form);
        StringAssert.Contains(form, "Input.ChoiceSet");
    }

    [TestMethod]
    public void FailedCredentialReadOrWriteForNewEndpointCannotKeepPreviousPassword()
    {
        var configuration = new VlcSettings(key => key == "http://first-pc:8080/" ? "first-secret" : throw new IOException(),
            (_, _) => throw new IOException());
        var settings = new Settings();
        configuration.AddTo(settings);
        settings.Update(Payload("http://first-pc:8080"));
        configuration.Apply();
        Assert.AreEqual("first-secret", configuration.GetOptions().Password);
        settings.Update(Payload("http://second-pc:8080", "second-secret"));
        configuration.Apply();
        Assert.AreEqual(string.Empty, configuration.GetOptions().Password);
        Assert.DoesNotContain("first-secret", settings.ToJson());
    }

    [TestMethod]
    public void PartialTreatmentSaveCannotMigrateALocalPasswordToARemoteEndpoint()
    {
        var vault = new Dictionary<string, string> { ["http"] = "local-secret" };
        var configuration = new VlcSettings(key => vault.GetValueOrDefault(key, ""), (key, password) => vault[key] = password);
        var settings = new Settings();
        configuration.AddTo(settings);
        settings.Update(Payload("http://remote-pc:8080"));
        configuration.Apply();
        settings.Update(new JsonObject { [Prefix + "LocalTreatment"] = "local" }.ToJsonString());
        configuration.Apply();
        settings.Update(Payload("http://other-pc:8080"));
        configuration.Apply();
        settings.Update(Payload("http://remote-pc:8080"));
        configuration.Apply();
        Assert.AreEqual(string.Empty, configuration.GetOptions().Password);
        Assert.IsFalse(vault.ContainsKey("http://remote-pc:8080/"));
    }

    [TestMethod]
    [DataRow("http://127.0.0.1:8080", "auto", true, true)]
    [DataRow("http://other-pc:8080", "auto", false, false)]
    [DataRow("http://other-pc:8080", "local", true, false)]
    [DataRow("http://127.0.0.1:8080", "remote", false, true)]
    public void TreatmentOverridesBehaviorWithoutChangingNativeLocality(string endpoint, string treatment, bool local, bool native)
    {
        var configuration = new VlcSettings(_ => "", (_, _) => { });
        var settings = new Settings();
        configuration.AddTo(settings);
        settings.Update(Payload(endpoint, treatment: treatment));
        configuration.Apply();
        Assert.AreEqual(local, configuration.GetOptions().TreatAsLocal);
        Assert.AreEqual(native, configuration.GetOptions().IsLocalConnection);
    }

    [TestMethod]
    public void InvalidServerUrlIsRejectedBeforeChangingSettingsOrWritingCredentials()
    {
        var saves = 0;
        var configuration = new VlcSettings(_ => "", (_, _) => saves++);
        var settings = new Settings();
        configuration.AddTo(settings);
        configuration.Apply();
        var before = configuration.GetOptions();
        var page = new MediaBackendConfigurationPage("vlc", "VLC", settings, configuration.Apply, NullLoggerFactory.Instance)
        {
            ValidateInputs = configuration.Validate,
        };
        var form = (IFormContent)page.GetContent().Single();
        var result = form.SubmitForm(Payload("http://user:embedded-secret@host:8080", "other-secret"), "{}");
        Assert.AreEqual(CommandResultKind.ShowToast, result.Kind);
        Assert.AreEqual(before, configuration.GetOptions());
        Assert.AreEqual(0, saves);
        Assert.DoesNotContain("embedded-secret", ((IToastArgs)result.Args!).Message);
    }

    private static string Payload(string endpoint, string password = "", string? treatment = null)
    {
        var payload = new JsonObject { [Prefix + "Endpoint"] = endpoint, [Prefix + "Password"] = password };
        if (treatment is not null) payload[Prefix + "LocalTreatment"] = treatment;
        return payload.ToJsonString();
    }
}