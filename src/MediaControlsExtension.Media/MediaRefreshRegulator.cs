// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Media;

[Flags]
internal enum MediaRefreshReason
{
    None = 0,
    ObservationsChanged = 1 << 0,
    SessionsChanged = 1 << 1,
    CurrentSessionChanged = 1 << 2,
    CommandCompleted = 1 << 3,
    CommandSettle = 1 << 4,
    PredictionExpired = 1 << 5,
}

internal enum MediaRefreshMode
{
    Burst,
    Sustained,
}

internal readonly record struct MediaRefreshAdmission(
    TimeSpan Delay,
    MediaRefreshMode Mode,
    int BurstCredits,
    int TopologyBurstCredits);

internal sealed record MediaRefreshPolicy(
    TimeSpan BurstInterval,
    TimeSpan SustainedInterval,
    TimeSpan QuietPeriod,
    int BurstCapacity,
    int TopologyBurstCapacity)
{
    public static MediaRefreshPolicy Default { get; } = new(
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(1),
        3,
        2);

    public void Validate()
    {
        if (this.BurstInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(this.BurstInterval));
        }

        if (this.SustainedInterval < this.BurstInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(this.SustainedInterval),
                "The sustained interval cannot be shorter than the burst interval.");
        }

        if (this.QuietPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(this.QuietPeriod));
        }

        if (this.BurstCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(this.BurstCapacity));
        }

        if (this.TopologyBurstCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(this.TopologyBurstCapacity));
        }
    }
}

/// <summary>
/// Decides when a requested media snapshot may run. Request coalescing remains
/// owned by <see cref="MediaService"/>; credits are consumed only by snapshots
/// that actually execute.
/// </summary>
internal sealed class MediaRefreshRegulator
{
    private const MediaRefreshReason TopologyReasons =
        MediaRefreshReason.SessionsChanged |
        MediaRefreshReason.CurrentSessionChanged;

    private readonly MediaRefreshPolicy _policy;
    private readonly TimeProvider _timeProvider;
    private int _burstCredits;
    private int _topologyBurstCredits;
    private bool _hasExecutionTimestamp;
    private bool _hasRequestTimestamp;
    private long _lastExecutionTimestamp;
    private long _lastRequestTimestamp;

    public MediaRefreshRegulator(
        TimeProvider timeProvider,
        MediaRefreshPolicy policy)
    {
        this._timeProvider = timeProvider;
        this._policy = policy;
        this._policy.Validate();
        this.ResetBurstCredits();
    }

    public bool RegisterRequest(long timestamp)
    {
        var startsNewBurst = !this._hasRequestTimestamp ||
            this._timeProvider.GetElapsedTime(this._lastRequestTimestamp, timestamp) >=
                this._policy.QuietPeriod;
        if (startsNewBurst)
        {
            this.ResetBurstCredits();
        }

        this._lastRequestTimestamp = timestamp;
        this._hasRequestTimestamp = true;
        return startsNewBurst;
    }

    public MediaRefreshAdmission GetAdmission(
        long timestamp,
        MediaRefreshReason reasons)
    {
        var topologyBurstAvailable =
            (reasons & TopologyReasons) != 0 &&
            this._topologyBurstCredits > 0;
        var usesBurst = this._burstCredits > 0 || topologyBurstAvailable;
        var interval = usesBurst
            ? this._policy.BurstInterval
            : this._policy.SustainedInterval;
        var delay = TimeSpan.Zero;
        if (this._hasExecutionTimestamp)
        {
            var elapsed = this._timeProvider.GetElapsedTime(
                this._lastExecutionTimestamp,
                timestamp);
            if (elapsed < interval)
            {
                delay = interval - elapsed;
            }
        }

        return new(
            delay,
            usesBurst ? MediaRefreshMode.Burst : MediaRefreshMode.Sustained,
            this._burstCredits,
            this._topologyBurstCredits);
    }

    public void RegisterExecution(
        long timestamp,
        MediaRefreshReason reasons)
    {
        var usedGeneralBurstCredit = this._burstCredits > 0;
        if (this._burstCredits > 0)
        {
            this._burstCredits--;
        }

        if (!usedGeneralBurstCredit &&
            (reasons & TopologyReasons) != 0 &&
            this._topologyBurstCredits > 0)
        {
            this._topologyBurstCredits--;
        }

        this._lastExecutionTimestamp = timestamp;
        this._hasExecutionTimestamp = true;
    }

    private void ResetBurstCredits()
    {
        this._burstCredits = this._policy.BurstCapacity;
        this._topologyBurstCredits = this._policy.TopologyBurstCapacity;
    }
}
