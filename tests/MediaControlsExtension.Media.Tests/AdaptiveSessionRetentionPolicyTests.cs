// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Infrastructure.Gsmtc;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class AdaptiveSessionRetentionPolicyTests
{
    private static readonly TimeSpan ProbeGracePeriod = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaximumGracePeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RecentRemovalWindow = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan EvidenceLifetime = TimeSpan.FromMinutes(5);

    [TestMethod]
    public void UnknownApplicationUsesProbeGracePeriod()
    {
        var policy = CreatePolicy();

        var gracePeriod = policy.GetGracePeriod("browser", TimeSpan.Zero, isUnambiguous: true);

        Assert.AreEqual(ProbeGracePeriod, gracePeriod);
        Assert.AreEqual(RecentRemovalWindow, policy.RecentRemovalWindow);
    }

    [TestMethod]
    public void StrongRecreationWithoutMissingIntervalKeepsProbeGracePeriod()
    {
        var policy = CreatePolicy();

        var graceIncreased = policy.RecordRecreation(
            "browser",
            SessionRecreationEvidence.Strong,
            missingDuration: null,
            TimeSpan.FromSeconds(1));

        Assert.IsFalse(graceIncreased);
        Assert.AreEqual(
            ProbeGracePeriod,
            policy.GetGracePeriod("browser", TimeSpan.FromSeconds(2), isUnambiguous: true));
    }

    [TestMethod]
    public void TwoWeakRecreationsUseLargestObservedMissingInterval()
    {
        var policy = CreatePolicy();

        Assert.IsFalse(policy.RecordRecreation(
            "browser",
            SessionRecreationEvidence.Weak,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1)));
        Assert.AreEqual(
            ProbeGracePeriod,
            policy.GetGracePeriod("browser", TimeSpan.FromSeconds(2), isUnambiguous: true));

        Assert.IsTrue(policy.RecordRecreation(
            "browser",
            SessionRecreationEvidence.Weak,
            TimeSpan.FromMilliseconds(800),
            TimeSpan.FromSeconds(3)));
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(1500),
            policy.GetGracePeriod("browser", TimeSpan.FromSeconds(4), isUnambiguous: true));
    }

    [TestMethod]
    public void LearnedDirectReplacementUsesLaterObservedMissingInterval()
    {
        var policy = CreatePolicy();
        policy.RecordRecreation(
            "browser",
            SessionRecreationEvidence.Strong,
            missingDuration: null,
            TimeSpan.Zero);

        policy.RecordRecreation(
            "browser",
            SessionRecreationEvidence.Weak,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2));

        Assert.AreEqual(
            TimeSpan.FromMilliseconds(1500),
            policy.GetGracePeriod("browser", TimeSpan.FromSeconds(3), isUnambiguous: true));
    }

    [TestMethod]
    public void ObservedMissingDurationGrowsLearnedGraceWithMargin()
    {
        var policy = CreatePolicy();

        policy.RecordRecreation(
            "browser",
            SessionRecreationEvidence.Strong,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2));

        Assert.AreEqual(
            TimeSpan.FromMilliseconds(1500),
            policy.GetGracePeriod("browser", TimeSpan.FromSeconds(3), isUnambiguous: true));
    }

    [TestMethod]
    public void ObservedMissingDurationIsCappedAtMaximumGracePeriod()
    {
        var policy = CreatePolicy();

        policy.RecordRecreation(
            "browser",
            SessionRecreationEvidence.Strong,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(2));

        Assert.AreEqual(
            MaximumGracePeriod,
            policy.GetGracePeriod("browser", TimeSpan.FromSeconds(3), isUnambiguous: true));
    }

    [TestMethod]
    public void LearnedGraceIsNotUsedForAmbiguousApplicationSessions()
    {
        var policy = CreatePolicy();
        policy.RecordRecreation(
            "browser",
            SessionRecreationEvidence.Strong,
            missingDuration: null,
            TimeSpan.Zero);

        var gracePeriod = policy.GetGracePeriod(
            "browser",
            TimeSpan.FromSeconds(1),
            isUnambiguous: false);

        Assert.AreEqual(ProbeGracePeriod, gracePeriod);
    }

    [TestMethod]
    public void LearnedEvidenceExpiresAfterQuietPeriod()
    {
        var policy = CreatePolicy();
        policy.RecordRecreation(
            "browser",
            SessionRecreationEvidence.Strong,
            missingDuration: null,
            TimeSpan.Zero);

        var gracePeriod = policy.GetGracePeriod(
            "browser",
            EvidenceLifetime,
            isUnambiguous: true);

        Assert.AreEqual(ProbeGracePeriod, gracePeriod);
    }

    private static AdaptiveSessionRetentionPolicy CreatePolicy() => new(
        ProbeGracePeriod,
        MaximumGracePeriod,
        RecentRemovalWindow,
        EvidenceLifetime);
}