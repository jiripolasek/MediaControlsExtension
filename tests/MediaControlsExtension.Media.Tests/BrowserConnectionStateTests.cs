// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Text.Json;
using System.Text.Json.Nodes;
using JPSoftworks.MediaControlsExtension.Media.Browser;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class BrowserConnectionStateTests
{
    [TestMethod]
    public void ConnectionSourceTracksWarmupRecoveryGraceAndWindowsFallback()
    {
        var policy = new BrowserFallbackPolicy();
        var health = new BrowserHealth("edge", "MSEdge:profile", "Edge profile", "MSEdge", TimeSpan.Zero, true)
        {
            BrowserName = "Microsoft Edge", ExecutablePath = @"C:\Browser\edge.exe"
        };
        var catalog = new BrowserCatalog(1, [], [health]);
        var warming = policy.Evaluate(TimeSpan.Zero, catalog).Connection.Connections.Single();
        Assert.AreEqual(MediaConnectionSource.Fallback, warming.Source);
        Assert.AreEqual(MediaConnectionStatus.Connecting, warming.Status);
        policy.Evaluate(TimeSpan.FromSeconds(5), catalog);
        var active = policy.Evaluate(TimeSpan.FromSeconds(10), catalog);
        var connected = active.Connection.Connections.Single();
        Assert.AreEqual(MediaConnectionSource.Provider, connected.Source);
        Assert.AreEqual("Microsoft Edge", connected.ApplicationName);
        Assert.AreEqual(health.ExecutablePath, connected.ApplicationPath);
        Assert.AreEqual("MSEdge", connected.ApplicationId);
        Assert.HasCount(2, active.Claims);
        var lost = policy.Evaluate(TimeSpan.FromSeconds(11), disconnected: true).Connection.Connections.Single();
        Assert.AreEqual(MediaConnectionSource.Reconnecting, lost.Source);
        Assert.AreEqual(MediaConnectionStatus.Connecting, lost.Status);
        for (var second = 12; second < 70; second++) policy.Evaluate(TimeSpan.FromSeconds(second));
        var fallback = policy.Evaluate(TimeSpan.FromSeconds(70));
        Assert.AreEqual(MediaConnectionSource.Fallback, fallback.Connection.Connections.Single().Source);
        Assert.AreEqual(MediaConnectionStatus.Disconnected, fallback.Connection.Connections.Single().Status);
        Assert.IsEmpty(fallback.Claims);
    }

    [TestMethod]
    public void UnknownBrowserCanBeConnectedWhileWindowsRemainsTheSource()
    {
        var policy = new BrowserFallbackPolicy();
        var health = new BrowserHealth("brave", "Brave:profile", "Brave profile", "", TimeSpan.Zero, true)
        {
            BrowserName = "Brave", ExecutablePath = @"C:\Browser\brave.exe"
        };
        var state = policy.Evaluate(TimeSpan.Zero, new BrowserCatalog(1, [], [health]));
        var connection = state.Connection.Connections.Single();
        Assert.AreEqual(MediaConnectionStatus.Connected, connection.Status);
        Assert.AreEqual(MediaConnectionSource.Fallback, connection.Source);
        Assert.AreEqual("Brave", connection.ApplicationName);
        Assert.AreEqual(health.ExecutablePath, connection.ApplicationPath);
        Assert.AreEqual(connection.DiagnosticMessage, state.Connection.DiagnosticMessage);
        Assert.IsEmpty(state.Claims);
        Assert.IsEmpty(state.Targets);
        var disconnected = policy.Evaluate(TimeSpan.FromSeconds(1), disconnected: true).Connection.Connections.Single();
        Assert.AreEqual(MediaConnectionStatus.Disconnected, disconnected.Status);
        Assert.AreEqual(MediaConnectionSource.Fallback, disconnected.Source);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CatalogRetainsBrowserIdentityMetadataWithoutGuessingNativeClaims(bool metadata)
    {
        var connection = new JsonObject
        {
            ["connectionId"] = "connection", ["profileId"] = "profile", ["browser"] = "Brave",
            ["applicationId"] = "", ["heartbeatAgeMs"] = 0, ["hasSnapshot"] = true,
            ["sessions"] = new JsonArray(), ["tabs"] = new JsonArray()
        };
        if (metadata) connection["browserExecutable"] = @"C:\Browser\brave.exe";
        using var document = JsonDocument.Parse(new JsonObject
        {
            ["type"] = "catalog", ["version"] = 2, ["revision"] = 1, ["connections"] = new JsonArray(connection)
        }.ToJsonString());
        var health = BrowserCatalog.Parse(document.RootElement).Health.Single();
        Assert.AreEqual("Brave", health.BrowserName);
        Assert.AreEqual(metadata ? @"C:\Browser\brave.exe" : "", health.ExecutablePath);
        Assert.AreEqual("", health.ApplicationId);
    }

    [TestMethod]
    public void NativeExecutableSeparatesMatchingProfileIdsAcrossUnknownBrowsers()
    {
        var connections = new JsonArray();
        foreach (var name in new[] { "Brave", "Other" }) connections.Add(new JsonObject
        {
            ["connectionId"] = name, ["profileId"] = "same-profile", ["browser"] = "Custom Chromium",
            ["browserExecutable"] = $@"C:\{name}\browser.exe", ["applicationId"] = "",
            ["heartbeatAgeMs"] = 0, ["hasSnapshot"] = true, ["sessions"] = new JsonArray(), ["tabs"] = new JsonArray()
        });
        using var document = JsonDocument.Parse(new JsonObject
        {
            ["type"] = "catalog", ["version"] = 2, ["revision"] = 1, ["connections"] = connections
        }.ToJsonString());
        var catalog = BrowserCatalog.Parse(document.RootElement);
        Assert.AreEqual(2, catalog.Health.Select(profile => profile.Origin).Distinct().Count());
        var state = new BrowserFallbackPolicy().Evaluate(TimeSpan.Zero, catalog);
        Assert.HasCount(2, state.Connection.Connections);
        Assert.IsEmpty(state.Claims);
    }
}