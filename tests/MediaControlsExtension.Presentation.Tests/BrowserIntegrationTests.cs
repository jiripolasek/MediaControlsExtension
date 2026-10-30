// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Text.Json.Nodes;
using JPSoftworks.MediaControlsExtension.BrowserIntegration;
using JPSoftworks.MediaControlsExtension.Commands;
using JPSoftworks.MediaControlsExtension.Pages;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.MediaControlsExtension.Presentation.Tests;

[TestClass]
public sealed class BrowserIntegrationTests
{
    [TestMethod]
    public void RemovalRequiresTheExplicitFormAction()
    {
        var page = new BrowserCompanionPage(_ => { }, () => false, NullLoggerFactory.Instance);
        Assert.HasCount(0, page.Commands, "Removal must not become the page's default Enter action.");
    }

    [TestMethod]
    [DataRow(false, false, true)]
    [DataRow(false, true, true)]
    [DataRow(true, false, true)]
    [DataRow(true, true, false)]
    public void EnabledStatusRequiresBothSourceSelectionAndRegistration(bool sourceEnabled, bool registered, bool showEnable)
    {
        var page = new BrowserCompanionPage(_ => { }, () => sourceEnabled, NullLoggerFactory.Instance,
            () => (new string('a', 32), registered));
        var form = Assert.IsInstanceOfType<IFormContent>(page.GetContent().Single());

        Assert.AreEqual(showEnable, EnableAction(form) is not null);
        Assert.AreEqual(!showEnable, BodyElement(form, "integrationStatus")["isVisible"]!.GetValue<bool>());
        Assert.AreEqual(BrowserCompanionPage.Text("EnabledStatus"), BodyElement(form, "integrationStatus")["text"]!.GetValue<string>());
    }

    [TestMethod]
    public void EnableAndRemoveRefreshTheSetupState()
    {
        var enabled = false;
        var registered = false;
        var page = new BrowserCompanionPage(value => enabled = value, () => enabled, NullLoggerFactory.Instance,
            () => (new string('a', 32), registered), (remove, _, _) =>
            {
                registered = !remove;
                return Task.CompletedTask;
            });
        var notifications = 0;
        page.ItemsChanged += (_, _) => notifications++;
        var form = Assert.IsInstanceOfType<IFormContent>(page.GetContent().Single());

        form.SubmitForm("{}", EnableAction(form)!["data"]!.ToJsonString());

        Assert.IsTrue(enabled);
        Assert.AreEqual(1, notifications);
        form = Assert.IsInstanceOfType<IFormContent>(page.GetContent().Single());
        Assert.IsNull(EnableAction(form));
        Assert.IsTrue(BodyElement(form, "integrationStatus")["isVisible"]!.GetValue<bool>());

        form.SubmitForm("{}", "{\"remove\":true}");

        Assert.IsFalse(enabled);
        Assert.AreEqual(2, notifications);
        form = Assert.IsInstanceOfType<IFormContent>(page.GetContent().Single());
        Assert.IsNotNull(EnableAction(form));
        Assert.IsFalse(BodyElement(form, "integrationStatus")["isVisible"]!.GetValue<bool>());
        Assert.AreEqual(BrowserCompanionPage.Text("Enable"), EnableAction(form)!["title"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailedSetupDoesNotChangeTheDisplayedState(bool remove)
    {
        var enabled = remove;
        var page = new BrowserCompanionPage(value => enabled = value, () => enabled, NullLoggerFactory.Instance,
            () => (new string('a', 32), remove), (_, _, _) => Task.FromException(new IOException("Fixture setup failure.")));
        var form = Assert.IsInstanceOfType<IFormContent>(page.GetContent().Single());

        var result = form.SubmitForm("{}", new JsonObject { ["remove"] = remove }.ToJsonString());

        Assert.AreEqual(CommandResultKind.ShowToast, result.Kind);
        Assert.AreEqual(remove, enabled);
        form = Assert.IsInstanceOfType<IFormContent>(page.GetContent().Single());
        Assert.AreEqual(!remove, EnableAction(form) is not null);
    }

    [TestMethod]
    [DataRow("ready", true)]
    [DataRow("missingManifest", false)]
    [DataRow("invalidJson", false)]
    [DataRow("invalidShape", false)]
    [DataRow("invalidOrigin", false)]
    [DataRow("emptyOrigins", false)]
    [DataRow("invalidHost", false)]
    [DataRow("legacyHost", false)]
    [DataRow("legacyFirefoxHost", false)]
    [DataRow("legacyFirefoxId", false)]
    [DataRow("missingBridge", false)]
    [DataRow("missingChrome", false)]
    [DataRow("wrongEdge", false)]
    [DataRow("missingFirefox", false)]
    [DataRow("wrongFirefoxId", false)]
    [DataRow("firefoxOnly", true)]
    public void RegistrationMustBeComplete(string scenario, bool expected)
    {
        var directory = Directory.CreateTempSubdirectory("MediaControls.BrowserSetup.").FullName;
        var manifestPath = Path.Combine(directory, "native-host.json");
        var firefoxPath = Path.Combine(directory, "firefox-native-host.json");
        var bridgePath = Path.Combine(directory, "bridge.exe");
        var id = new string('a', 32);
        try
        {
            var manifest = new JsonObject
            {
                ["name"] = scenario switch
                {
                    "invalidHost" => "another.host",
                    "legacyHost" => "com.jpsoftworks.media_controls_spike",
                    _ => "com.jiripolasek.media_controls",
                },
                ["path"] = bridgePath,
                ["type"] = "stdio",
                ["allowed_origins"] = new JsonArray($"chrome-extension://{id}/"),
            };
            if (scenario == "invalidOrigin") { manifest["allowed_origins"]!.AsArray().Add(42); }
            if (scenario == "emptyOrigins") { manifest["allowed_origins"] = new JsonArray(); }
            if (scenario != "missingBridge") { File.WriteAllText(bridgePath, "fixture"); }
            if (scenario is not ("missingManifest" or "firefoxOnly"))
            {
                File.WriteAllText(manifestPath, scenario switch
                {
                    "invalidJson" => "{",
                    "invalidShape" => "[]",
                    _ => manifest.ToJsonString(),
                });
            }

            if (scenario != "missingManifest")
            {
                File.WriteAllText(firefoxPath, new JsonObject
                {
                    ["name"] = scenario == "legacyFirefoxHost" ? "com.jpsoftworks.media_controls_spike" : "com.jiripolasek.media_controls",
                    ["path"] = bridgePath, ["type"] = "stdio",
                    ["allowed_extensions"] = new JsonArray(scenario switch
                    {
                        "wrongFirefoxId" => "wrong@example.org",
                        "legacyFirefoxId" => "media-controls-companion@jpsoftworks.com",
                        _ => "media-controls-companion@jiripolasek.com",
                    }),
                }.ToJsonString());
            }

            var registration = BrowserCompanionPage.ReadRegistration(manifestPath, key =>
            {
                Assert.EndsWith(@"\com.jiripolasek.media_controls", key);
                if (key.Contains("Mozilla", StringComparison.Ordinal)) { return scenario == "missingFirefox" ? null : firefoxPath; }
                if (scenario == "missingChrome" && key.Contains("Google\\Chrome", StringComparison.Ordinal)) { return null; }
                if (scenario == "wrongEdge" && key.Contains("Microsoft\\Edge", StringComparison.Ordinal)) { return bridgePath; }
                return manifestPath;
            });

            Assert.AreEqual(expected, registration.IsRegistered);
            if (expected) { Assert.AreEqual(scenario == "firefoxOnly" ? string.Empty : id, registration.ExtensionIds); }
        }
        finally
        {
            File.Delete(manifestPath);
            File.Delete(firefoxPath);
            File.Delete(bridgePath);
            Directory.Delete(directory);
        }
    }

    private static JsonNode? EnableAction(IFormContent form) => BodyElement(form, "setupActions")["actions"]!.AsArray()
        .SingleOrDefault(action => action?["id"]?.GetValue<string>() == "enableIntegration");

    private static JsonNode BodyElement(IFormContent form, string id) => JsonNode.Parse(form.TemplateJson)!["body"]!.AsArray()
        .Single(element => element?["id"]?.GetValue<string>() == id)!;

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SourceSelectionChangesOnlyAfterSuccessfulHelperCompletion(bool remove)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changes = new List<bool>();
        var ids = new string('a', 32);
        var command = new BrowserIntegrationCommand(remove, ids, changes.Add, NullLoggerFactory.Instance,
            async (actualRemove, actualIds, token) =>
            {
                Assert.AreEqual(remove, actualRemove);
                Assert.AreEqual(ids, actualIds);
                started.SetResult();
                await completed.Task.WaitAsync(token);
            });
        var invocation = Task.Run(command.Invoke);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.HasCount(0, changes);
        }
        finally
        {
            completed.TrySetResult();
            await invocation.WaitAsync(TimeSpan.FromSeconds(5));
        }
        CollectionAssert.AreEqual(new[] { !remove }, changes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void HelperFailurePreservesSourceSelection(bool remove)
    {
        var changes = new List<bool>();
        var command = new BrowserIntegrationCommand(remove, string.Empty, changes.Add, NullLoggerFactory.Instance,
            (_, _, _) => Task.FromException(new IOException("Fixture setup failure.")));
        var result = command.Invoke();
        Assert.AreEqual(CommandResultKind.ShowToast, result.Kind);
        Assert.HasCount(0, changes);
    }

    [TestMethod]
    [DataRow("*")]
    [DataRow("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/")]
    [DataRow("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaZ")]
    [DataRow("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void InvalidOriginsAreRejected(string value)
    {
        Assert.ThrowsExactly<ArgumentException>(() => PackagedBrowserIntegration.ParseExtensionIds(value));
    }

    [TestMethod]
    public void MultipleExtensionIdsAreNormalizedWithoutDuplicates()
    {
        var first = new string('a', 32);
        var second = new string('b', 32);
        CollectionAssert.AreEqual(new[] { first, second }, PackagedBrowserIntegration.ParseExtensionIds($" {first}, {second}\r\n{first}"));
    }
}