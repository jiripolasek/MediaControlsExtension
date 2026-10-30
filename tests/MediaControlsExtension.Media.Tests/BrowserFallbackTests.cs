// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Browser;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class BrowserFallbackTests
{
    private static readonly string[] GsmtcBackends = ["gsmtc", "gsmtc.worker"];
    [TestMethod]
    public void EmptyCatalogRequiresStableBrowserHeartbeatsBeforeClaimingBothGsmtcModes()
    {
        var policy = new BrowserFallbackPolicy();
        var catalog = Catalog(Health("edge")) with { Targets = [] };
        Assert.IsEmpty(policy.Evaluate(Seconds(0), catalog).Claims);
        Assert.IsEmpty(policy.Evaluate(Seconds(5), catalog).Claims);
        Assert.IsEmpty(policy.Evaluate(Seconds(9), catalog).Claims);
        var active = policy.Evaluate(Seconds(10), catalog);
        CollectionAssert.AreEquivalent(GsmtcBackends, active.Claims.Select(claim => claim.BackendId).ToArray());
        Assert.IsTrue(active.Claims.All(claim => claim.ApplicationId == "MSEdge"));
        Assert.IsEmpty(active.Targets);
        Assert.AreEqual(MediaConnectionStatus.Connected, active.Connection.Status);
    }

    [TestMethod]
    public void RelayCatalogsCannotSubstituteForBrowserHeartbeats()
    {
        var policy = new BrowserFallbackPolicy();
        policy.Evaluate(Seconds(0), Catalog(Health("edge")));
        for (var second = 1; second <= 65; second++)
        {
            var view = policy.Evaluate(Seconds(second), Catalog(Health("edge") with { HeartbeatAge = Seconds(second) }));
            Assert.IsEmpty(view.Claims);
            Assert.IsEmpty(view.Targets);
        }
    }

    [TestMethod]
    [DataRow(false, 0L)]
    [DataRow(true, -1L)]
    public void MissingCatalogOrHeartbeatNeverTakesOwnership(bool hasSnapshot, long age)
    {
        var policy = new BrowserFallbackPolicy();
        var catalog = Catalog(Health("edge") with { HasSnapshot = hasSnapshot, HeartbeatAge = Seconds(age) });
        for (var second = 0; second <= 70; second += 5)
        {
            var view = policy.Evaluate(Seconds(second), catalog);
            Assert.IsEmpty(view.Claims);
            Assert.IsEmpty(view.Targets);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OutageWithdrawsTargetsBeforeReleasingClaimsAtSixtySeconds(bool disconnected)
    {
        var policy = Ready();
        for (var second = 11; second < 70; second++)
        {
            var view = policy.Evaluate(Seconds(second), disconnected: disconnected && second == 11);
            Assert.HasCount(2, view.Claims, $"Claims released at second {second}.");
            Assert.HasCount(disconnected || second >= 30 ? 0 : 1, view.Targets);
        }
        var fallback = policy.Evaluate(Seconds(70));
        Assert.IsEmpty(fallback.Claims);
        Assert.IsEmpty(fallback.Targets);
        Assert.AreEqual(MediaConnectionStatus.Disconnected, fallback.Connection.Status);
    }

    [TestMethod]
    public void RecoveryUsesANewConnectionAndRestartsTheStabilityPeriod()
    {
        var policy = Ready();
        for (var second = 11; second <= 70; second++) policy.Evaluate(Seconds(second));
        Assert.IsEmpty(policy.Evaluate(Seconds(71), Catalog(Health("new"))).Claims);
        Assert.IsEmpty(policy.Evaluate(Seconds(76), Catalog(Health("new"))).Claims);
        policy.Evaluate(Seconds(77), disconnected: true);
        Assert.IsEmpty(policy.Evaluate(Seconds(78), Catalog(Health("newer"))).Claims);
        Assert.IsEmpty(policy.Evaluate(Seconds(83), Catalog(Health("newer"))).Claims);
        Assert.IsEmpty(policy.Evaluate(Seconds(87), Catalog(Health("newer"))).Claims);
        var recovered = policy.Evaluate(Seconds(88), Catalog(Health("newer")));
        Assert.HasCount(2, recovered.Claims);
        Assert.AreEqual("newer", recovered.Targets.Single().ConnectionId);
    }

    [TestMethod]
    public void HealthyProfileKeepsSharedBrowserClaimAndReportsLostProfile()
    {
        var policy = new BrowserFallbackPolicy();
        for (var second = 0; second <= 10; second += 5)
            policy.Evaluate(Seconds(second), Catalog(Health("one"), Health("two", profile: "second")));
        for (var second = 11; second <= 80; second++)
        {
            var view = policy.Evaluate(Seconds(second), Catalog(Health("one")));
            Assert.HasCount(2, view.Claims);
            Assert.AreEqual("one", view.Targets.Single().ConnectionId);
            Assert.AreEqual(MediaConnectionStatus.Connected, view.Connection.Status);
            Assert.AreEqual(MediaConnectionStatus.Connecting, view.Connection.Connections.Single(connection => connection.Id.EndsWith(":second", StringComparison.Ordinal)).Status);
        }
    }

    [TestMethod]
    public void BrowserFallbackIsIndependentOfOtherBrowserHeartbeats()
    {
        var policy = new BrowserFallbackPolicy();
        for (var second = 0; second <= 10; second += 5)
            policy.Evaluate(Seconds(second), Catalog(Health("edge"), Health("chrome", "Chrome")));
        for (var second = 11; second < 70; second++)
            Assert.HasCount(4, policy.Evaluate(Seconds(second), Catalog(Health("chrome", "Chrome"))).Claims);
        var fallback = policy.Evaluate(Seconds(70), Catalog(Health("chrome", "Chrome")));
        Assert.HasCount(2, fallback.Claims);
        Assert.IsTrue(fallback.Claims.All(claim => claim.ApplicationId == "Chrome"));
        Assert.AreEqual("chrome", fallback.Targets.Single().ConnectionId);
    }

    [TestMethod]
    public void UnknownBrowserIdentityNeverSuppressesWindowsOrPublishesCompanionTargets()
    {
        var policy = new BrowserFallbackPolicy();
        for (var second = 0; second <= 70; second += 5)
        {
            var view = policy.Evaluate(Seconds(second), Catalog(Health("unknown", "")));
            Assert.IsEmpty(view.Claims);
            Assert.IsEmpty(view.Targets);
        }
    }

    [TestMethod]
    public void ResumeWithdrawsStaleTargetsAndStartsFreshFallbackGrace()
    {
        var policy = Ready();
        var resumed = policy.Evaluate(Seconds(1000));
        Assert.IsEmpty(resumed.Targets);
        Assert.HasCount(2, resumed.Claims);
        for (var second = 1001; second < 1060; second++) Assert.HasCount(2, policy.Evaluate(Seconds(second)).Claims);
        Assert.IsEmpty(policy.Evaluate(Seconds(1060)).Claims);
    }

    [TestMethod]
    public void IdenticalMetadataAcrossTabsIsNotDeduplicated()
    {
        var policy = new BrowserFallbackPolicy();
        var catalog = Catalog(Health("one"), Health("two", profile: "second"));
        policy.Evaluate(Seconds(0), catalog);
        policy.Evaluate(Seconds(5), catalog);
        var view = policy.Evaluate(Seconds(10), catalog);
        Assert.HasCount(2, view.Targets);
        Assert.AreEqual(view.Targets[0].Properties, view.Targets[1].Properties);
    }

    [TestMethod]
    [DataRow(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "MSEdge")]
    [DataRow(@"C:\Program Files (x86)\Microsoft\Edge Dev\Application\msedge.exe", "MSEdgeDev")]
    [DataRow(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "Chrome")]
    [DataRow(@"C:\Portable\msedge.exe", "")]
    [DataRow(@"C:\Program Files\Microsoft\Edge Beta\Application\msedge.exe", "")]
    public void BrowserIdentityMapsOnlyKnownInstallationChannels(string path, string applicationId) =>
        Assert.AreEqual(applicationId, BrowserCompanion.BrowserIdentity.FromExecutable(path));

    private static TimeSpan Seconds(long value) => TimeSpan.FromSeconds(value);

    [TestMethod]
    [DataRow("308046B0AF4A39CB", "308046B0AF4A39CB")]
    [DataRow(null, "")]
    [DataRow("Chrome", "")]
    [DataRow("308046B0AF4A39CZ", "")]
    public void FirefoxIdentityUsesOnlyTheExactInstallRegistration(string? registeredId, string expected)
    {
        Assert.AreEqual(expected, BrowserCompanion.BrowserIdentity.FromExecutable(@"C:\Program Files\Mozilla Firefox\firefox.exe", path =>
        {
            Assert.AreEqual(@"C:\Program Files\Mozilla Firefox", path);
            return registeredId;
        }));
    }

    [TestMethod]
    public void FirefoxUsesTheSameDelayedFallbackAndIndependentRecovery()
    {
        const string firefoxId = "308046B0AF4A39CB";
        var policy = new BrowserFallbackPolicy();
        for (var second = 0; second <= 10; second += 5)
            policy.Evaluate(Seconds(second), Catalog(Health("firefox", firefoxId), Health("edge")));
        for (var second = 11; second < 70; second++)
            Assert.HasCount(4, policy.Evaluate(Seconds(second), Catalog(Health("edge"))).Claims);
        Assert.IsFalse(policy.Evaluate(Seconds(70), Catalog(Health("edge"))).Claims.Any(claim => claim.ApplicationId == firefoxId));
        for (var second = 71; second < 81; second++)
            Assert.IsFalse(policy.Evaluate(Seconds(second), Catalog(Health("reconnected", firefoxId), Health("edge")))
                .Claims.Any(claim => claim.ApplicationId == firefoxId));
        var recovered = policy.Evaluate(Seconds(81), Catalog(Health("reconnected", firefoxId), Health("edge")));
        Assert.HasCount(2, recovered.Claims.Where(claim => claim.ApplicationId == firefoxId).ToArray());
        Assert.AreEqual("reconnected", recovered.Targets.Single(target => target.ConnectionId == "reconnected").ConnectionId);
    }

    private static BrowserFallbackPolicy Ready()
    {
        var policy = new BrowserFallbackPolicy();
        policy.Evaluate(Seconds(0), Catalog(Health("edge")));
        policy.Evaluate(Seconds(5), Catalog(Health("edge")));
        Assert.HasCount(2, policy.Evaluate(Seconds(10), Catalog(Health("edge"))).Claims);
        return policy;
    }

    private static BrowserHealth Health(string id, string applicationId = "MSEdge", string profile = "profile") =>
        new(id, applicationId + ":" + profile, id, applicationId, TimeSpan.Zero, true);

    private static BrowserCatalog Catalog(params BrowserHealth[] health) => new(1,
        [.. health.Select(item => new BrowserTarget(item.ConnectionId, 1, "player", 1, item.Origin,
            MediaPropertiesSnapshot.Empty(new("Browser")) with { Title = "Same title", Artist = "Same artist" },
            new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, null),
            MediaPlaybackState.Paused, MediaCapabilities.ActivateSource, "", "", "Edge"))], [.. health]);
}