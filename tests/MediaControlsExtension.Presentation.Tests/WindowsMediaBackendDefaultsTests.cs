using System.Text.Json.Nodes;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.MediaControlsExtension.Presentation.Tests;

[TestClass]
public sealed class WindowsMediaBackendDefaultsTests
{
    private const string InternalKey = "jpsoftworks.mediacontrols.MediaBackends.gsmtc.Enabled";
    private const string WorkerKey = "jpsoftworks.mediacontrols.MediaBackends.gsmtc.worker.Enabled";
    private string _directory = null!;
    private string _path = null!;

    [TestInitialize]
    public void Initialize()
    {
        this._directory = Directory.CreateTempSubdirectory("MediaControls-default-tests-").FullName;
        this._path = Path.Combine(this._directory, "settings.json");
    }

    [TestCleanup]
    public void Cleanup()
    {
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(this._directory));
        Assert.StartsWith("MediaControls-default-tests-", Path.GetFileName(this._directory));
        Directory.Delete(this._directory, recursive: true);
    }

    [TestMethod]
    public void FreshInstallSelectsWorkerWithoutWritingSettingsOrStartingFactories()
    {
        Assert.AreEqual("gsmtc.worker", this.Load().EnabledIds.Single());
        Assert.IsFalse(File.Exists(this._path));
    }

    [TestMethod]
    [DataRow("{ broken json")]
    [DataRow("[]")]
    [DataRow("null")]
    [DataRow("{\"duplicate\":1,\"duplicate\":2}")]
    [DataRow("{\"unknown\":[{\"duplicate\":1,\"duplicate\":2}]}")]
    public void MalformedFileUsesDefaultsAndAnExplicitChoiceRepairsIt(string original)
    {
        File.WriteAllText(this._path, original);
        var settings = this.Load();
        Assert.AreEqual("gsmtc.worker", settings.EnabledIds.Single());
        Assert.IsNull(settings.GetDiagnostic("gsmtc.worker"));
        Assert.AreEqual(original, File.ReadAllText(this._path));

        settings.SetEnabled("gsmtc", true);

        Assert.AreEqual("gsmtc", this.Load().EnabledIds.Single());
        var backup = Directory.GetFiles(this._directory, "settings.json.invalid-*").Single();
        Assert.AreEqual(original, File.ReadAllText(backup));
        var saved = JsonNode.Parse(File.ReadAllText(this._path))!;
        Assert.AreEqual("true", saved[InternalKey]!.GetValue<string>());
        Assert.AreEqual("false", saved[WorkerKey]!.GetValue<string>());
    }

    [TestMethod]
    public void UnreadableFileStaysDisabledWithDiagnosticsAndFailedSavePreservesIt()
    {
        var original = new JsonObject { [WorkerKey] = "false", ["unrelated"] = "preserved" }.ToJsonString();
        File.WriteAllText(this._path, original);
        MediaBackendSettings settings;
        using (var locked = new FileStream(this._path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            settings = this.Load();
            Assert.IsEmpty(settings.EnabledIds);
            StringAssert.Contains(settings.GetDiagnostic("gsmtc.worker"), "Could not read");
            StringAssert.Contains(settings.GetDiagnostic("gsmtc"), "Could not read");
            Assert.Throws<IOException>(() => settings.SetEnabled("gsmtc", true));
            Assert.IsEmpty(settings.EnabledIds);
        }

        Assert.AreEqual(original, File.ReadAllText(this._path));
        settings.SetEnabled("gsmtc", true);
        Assert.AreEqual("gsmtc", settings.EnabledIds.Single());
        Assert.IsNull(settings.GetDiagnostic("gsmtc.worker"));
        Assert.IsNull(settings.GetDiagnostic("gsmtc"));
        Assert.AreEqual("preserved", JsonNode.Parse(File.ReadAllText(this._path))!["unrelated"]!.GetValue<string>());
        Assert.IsEmpty(Directory.GetFiles(this._directory, "settings.json.invalid-*"));
    }

    [TestMethod]
    [DataRow("true", true)]
    [DataRow("false", false)]
    public void LegacySelectionMovesToWorkerAndPreservesDisablement(string value, bool enabled)
    {
        var saved = new JsonObject { [InternalKey] = value, ["unrelated"] = "preserved" }.ToJsonString();
        File.WriteAllText(this._path, saved);
        var settings = this.Load();
        Assert.AreEqual(enabled ? 1 : 0, settings.EnabledIds.Length);
        if (enabled) { Assert.AreEqual("gsmtc.worker", settings.EnabledIds.Single()); }
        Assert.AreEqual(saved, File.ReadAllText(this._path));
    }

    [TestMethod]
    public async Task BackgroundRecoveryUsesTheSameWorkerMigrationAsStartup()
    {
        File.WriteAllText(this._path, new JsonObject { [InternalKey] = "true" }.ToJsonString());
        MediaBackendSettings settings;
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var locked = new FileStream(this._path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            settings = this.Load();
            settings.EnabledChanged += (_, _) => recovered.TrySetResult();
        }
        using (settings)
        {
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual("gsmtc.worker", settings.EnabledIds.Single());
            Assert.IsNull(settings.GetDiagnostic("gsmtc.worker"));
            Assert.AreEqual("gsmtc.worker", this.Load().EnabledIds.Single());
        }
    }

    [TestMethod]
    [DataRow("true", "false", "gsmtc")]
    [DataRow("false", "true", "gsmtc.worker")]
    [DataRow("false", "false", "")]
    public void ExplicitModeChoicesArePreserved(string internalValue, string workerValue, string selected)
    {
        File.WriteAllText(this._path, new JsonObject { [InternalKey] = internalValue, [WorkerKey] = workerValue }.ToJsonString());
        Assert.AreEqual(selected, string.Join(',', this.Load().EnabledIds));
    }

    [TestMethod]
    public void InvalidLegacySelectionStaysInactiveAndCanBeRepaired()
    {
        File.WriteAllText(this._path, new JsonObject { [InternalKey] = "invalid" }.ToJsonString());
        var settings = this.Load();
        Assert.IsEmpty(settings.EnabledIds);
        Assert.IsNotNull(settings.GetDiagnostic("gsmtc.worker"));
        settings.SetEnabled("gsmtc.worker", true);
        Assert.AreEqual("gsmtc.worker", this.Load().EnabledIds.Single());
    }

    [TestMethod]
    public void ChoosingInternalAfterMigrationIsPersistedAsAnExplicitMode()
    {
        File.WriteAllText(this._path, new JsonObject { [InternalKey] = "true" }.ToJsonString());
        this.Load().SetEnabled("gsmtc", true);
        Assert.AreEqual("gsmtc", this.Load().EnabledIds.Single());
        var saved = JsonNode.Parse(File.ReadAllText(this._path))!;
        Assert.AreEqual("true", saved[InternalKey]!.GetValue<string>());
        Assert.AreEqual("false", saved[WorkerKey]!.GetValue<string>());
    }

    private MediaBackendSettings Load()
    {
        var registry = new MediaBackendRegistry()
            .Register(new("gsmtc", "Internal", "", _ => throw new InvalidOperationException()) { ExclusiveGroup = "windows" })
            .Register(new("gsmtc.worker", "Worker", "", _ => throw new InvalidOperationException(), true) { ExclusiveGroup = "windows" });
        var store = new SettingsStore(this._path, NullLogger.Instance);
        return new(registry, store, WindowsMediaBackendDefaults.Normalize);
    }
}