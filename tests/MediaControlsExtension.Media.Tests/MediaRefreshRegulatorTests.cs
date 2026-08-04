// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class MediaRefreshRegulatorTests
{
    private static readonly MediaRefreshPolicy Policy = new(
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(1),
        3,
        2);

    [TestMethod]
    public void NotificationsDoNotConsumeBurstCreditsUntilARefreshExecutes()
    {
        var timeProvider = new ManualTimeProvider();
        var regulator = new MediaRefreshRegulator(timeProvider, Policy);

        regulator.RegisterRequest(timeProvider.GetTimestamp());
        for (var index = 0; index < 100; index++)
        {
            regulator.RegisterRequest(timeProvider.GetTimestamp());
        }

        var admission = regulator.GetAdmission(
            timeProvider.GetTimestamp(),
            MediaRefreshReason.ObservationsChanged);

        Assert.AreEqual(MediaRefreshMode.Burst, admission.Mode);
        Assert.AreEqual(3, admission.BurstCredits);
        Assert.AreEqual(TimeSpan.Zero, admission.Delay);
    }

    [TestMethod]
    public void TopologyCreditsCoverTeardownAfterTheGeneralBurstIsExhausted()
    {
        var timeProvider = new ManualTimeProvider();
        var regulator = new MediaRefreshRegulator(timeProvider, Policy);

        for (var index = 0; index < Policy.BurstCapacity; index++)
        {
            regulator.RegisterRequest(timeProvider.GetTimestamp());
            var burstAdmission = regulator.GetAdmission(
                timeProvider.GetTimestamp(),
                MediaRefreshReason.ObservationsChanged);
            Assert.AreEqual(MediaRefreshMode.Burst, burstAdmission.Mode);
            regulator.RegisterExecution(
                timeProvider.GetTimestamp(),
                MediaRefreshReason.ObservationsChanged);
            timeProvider.Advance(Policy.BurstInterval);
        }

        regulator.RegisterRequest(timeProvider.GetTimestamp());
        var sustainedAdmission = regulator.GetAdmission(
            timeProvider.GetTimestamp(),
            MediaRefreshReason.ObservationsChanged);
        Assert.AreEqual(MediaRefreshMode.Sustained, sustainedAdmission.Mode);
        Assert.AreEqual(TimeSpan.FromMilliseconds(200), sustainedAdmission.Delay);

        regulator.RegisterRequest(timeProvider.GetTimestamp());
        var firstTopologyAdmission = regulator.GetAdmission(
            timeProvider.GetTimestamp(),
            MediaRefreshReason.SessionsChanged);
        Assert.AreEqual(MediaRefreshMode.Burst, firstTopologyAdmission.Mode);
        Assert.AreEqual(TimeSpan.Zero, firstTopologyAdmission.Delay);
        regulator.RegisterExecution(
            timeProvider.GetTimestamp(),
            MediaRefreshReason.SessionsChanged);

        timeProvider.Advance(Policy.BurstInterval);
        regulator.RegisterRequest(timeProvider.GetTimestamp());
        var secondTopologyAdmission = regulator.GetAdmission(
            timeProvider.GetTimestamp(),
            MediaRefreshReason.CurrentSessionChanged);
        Assert.AreEqual(MediaRefreshMode.Burst, secondTopologyAdmission.Mode);
        regulator.RegisterExecution(
            timeProvider.GetTimestamp(),
            MediaRefreshReason.CurrentSessionChanged);

        timeProvider.Advance(Policy.BurstInterval);
        regulator.RegisterRequest(timeProvider.GetTimestamp());
        var exhaustedAdmission = regulator.GetAdmission(
            timeProvider.GetTimestamp(),
            MediaRefreshReason.SessionsChanged);
        Assert.AreEqual(MediaRefreshMode.Sustained, exhaustedAdmission.Mode);
        Assert.AreEqual(TimeSpan.FromMilliseconds(200), exhaustedAdmission.Delay);
    }

    [TestMethod]
    public void TopologyDuringTheGeneralBurstDoesNotSpendTheReserve()
    {
        var timeProvider = new ManualTimeProvider();
        var regulator = new MediaRefreshRegulator(timeProvider, Policy);

        regulator.RegisterRequest(timeProvider.GetTimestamp());
        regulator.RegisterExecution(
            timeProvider.GetTimestamp(),
            MediaRefreshReason.SessionsChanged);

        var admission = regulator.GetAdmission(
            timeProvider.GetTimestamp(),
            MediaRefreshReason.CurrentSessionChanged);

        Assert.AreEqual(Policy.BurstCapacity - 1, admission.BurstCredits);
        Assert.AreEqual(
            Policy.TopologyBurstCapacity,
            admission.TopologyBurstCredits);
    }

    [TestMethod]
    public void QuietPeriodRestoresBothBurstBudgets()
    {
        var timeProvider = new ManualTimeProvider();
        var regulator = new MediaRefreshRegulator(timeProvider, Policy);

        regulator.RegisterRequest(timeProvider.GetTimestamp());
        for (var index = 0; index < Policy.BurstCapacity; index++)
        {
            regulator.RegisterExecution(
                timeProvider.GetTimestamp(),
                MediaRefreshReason.ObservationsChanged);
            timeProvider.Advance(Policy.BurstInterval);
            regulator.RegisterRequest(timeProvider.GetTimestamp());
        }

        timeProvider.Advance(Policy.QuietPeriod);
        var startsNewBurst = regulator.RegisterRequest(timeProvider.GetTimestamp());
        var admission = regulator.GetAdmission(
            timeProvider.GetTimestamp(),
            MediaRefreshReason.SessionsChanged);

        Assert.IsTrue(startsNewBurst);
        Assert.AreEqual(MediaRefreshMode.Burst, admission.Mode);
        Assert.AreEqual(Policy.BurstCapacity, admission.BurstCredits);
        Assert.AreEqual(Policy.TopologyBurstCapacity, admission.TopologyBurstCredits);
        Assert.AreEqual(TimeSpan.Zero, admission.Delay);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref this._timestamp);

        public void Advance(TimeSpan elapsed)
        {
            Interlocked.Add(ref this._timestamp, elapsed.Ticks);
        }
    }
}
