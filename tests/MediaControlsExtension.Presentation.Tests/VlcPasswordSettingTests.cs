// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Text.Json.Nodes;
using JPSoftworks.MediaControlsExtension.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JPSoftworks.MediaControlsExtension.Presentation.Tests;

[TestClass]
public sealed class VlcPasswordSettingTests
{
    [TestMethod]
    public void PasswordIsSavedOutsideSettingsAndNeverReturnedToTheForm()
    {
        var saved = "initial-secret";
        var setting = new VlcPasswordSetting("vlc.password", "Password", "Description", "Placeholder",
            () => saved, password => saved = password);
        Assert.AreEqual(saved, setting.Password);
        Assert.AreEqual(string.Empty, setting.ToDictionary()["value"]);
        Assert.AreEqual("password", setting.ToDictionary()["style"]);
        Assert.IsNull(JsonNode.Parse("{" + setting.ToState() + "}")!["vlc.password"]);

        setting.Update(new JsonObject { ["vlc.password"] = "replacement-secret" });
        Assert.AreEqual("replacement-secret", saved);
        Assert.AreEqual(saved, setting.Password);
        Assert.AreEqual(string.Empty, setting.ToDictionary()["value"]);
        Assert.DoesNotContain(saved, setting.ToState());
    }

    [TestMethod]
    public void BlankSubmissionAndSettingsReloadPreserveTheSavedPassword()
    {
        var saves = 0;
        var setting = new VlcPasswordSetting("vlc.password", "Password", "Description", "Placeholder",
            () => "saved-secret", _ => saves++);
        setting.Update(new JsonObject { ["vlc.password"] = "" });
        setting.Update(new JsonObject { ["vlc.password"] = null });
        setting.Update(new JsonObject());
        Assert.AreEqual("saved-secret", setting.Password);
        Assert.AreEqual(0, saves);
    }

    [TestMethod]
    public void FailedCredentialWritePreservesThePreviousConnectionPassword()
    {
        var fail = true;
        string? error = null;
        var setting = new VlcPasswordSetting("vlc.password", "Password", "Description", "Placeholder",
            () => "saved-secret", _ =>
            {
                if (fail) throw new IOException("Credential store unavailable: replacement-secret.");
            }, message => error = message);
        setting.Update(new JsonObject { ["vlc.password"] = "replacement-secret" });
        Assert.AreEqual("saved-secret", setting.Password);
        Assert.IsNotNull(error);
        Assert.Contains(error, (string)setting.ToDictionary()["label"]);
        Assert.DoesNotContain("replacement-secret", error);
        Assert.DoesNotContain("saved-secret", error);
        Assert.AreEqual(string.Empty, setting.ToDictionary()["value"]);

        fail = false;
        setting.Update(new JsonObject { ["vlc.password"] = "replacement-secret" });
        Assert.AreEqual("replacement-secret", setting.Password);
        Assert.AreEqual("Description", setting.ToDictionary()["label"]);
    }

    [TestMethod]
    public void FailedCredentialReadLeavesSettingsUsableAndRecoversAfterSaving()
    {
        string? saved = null;
        var setting = new VlcPasswordSetting("vlc.password", "Password", "Description", "Placeholder",
            () => throw new UnauthorizedAccessException("Sensitive vault details."), password => saved = password);
        Assert.AreEqual(string.Empty, setting.Password);
        Assert.AreNotEqual("Description", setting.ToDictionary()["label"]);
        Assert.DoesNotContain("Sensitive vault details", (string)setting.ToDictionary()["label"]);

        setting.Update(new JsonObject { ["vlc.password"] = "replacement-secret" });
        Assert.AreEqual("replacement-secret", saved);
        Assert.AreEqual(saved, setting.Password);
        Assert.AreEqual("Description", setting.ToDictionary()["label"]);
    }

    [TestMethod]
    public void FailedErrorNotificationStillKeepsSettingsUsableAndTheErrorInTheForm()
    {
        var setting = new VlcPasswordSetting("vlc.password", "Password", "Description", "Placeholder",
            () => "saved-secret", _ => throw new IOException(), _ => throw new InvalidOperationException());
        setting.Update(new JsonObject { ["vlc.password"] = "replacement-secret" });
        Assert.AreEqual("saved-secret", setting.Password);
        Assert.AreNotEqual("Description", setting.ToDictionary()["label"]);
    }
}