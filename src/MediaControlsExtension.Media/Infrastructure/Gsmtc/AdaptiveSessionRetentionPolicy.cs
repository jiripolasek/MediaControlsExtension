// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Media.Infrastructure.Gsmtc;

internal enum SessionRecreationEvidence
{
    Weak,
    Strong,
}

/// <summary>
/// Learns which application identities recreate their native GSMTC sessions and
/// grants only those identities a longer transient-removal grace period.
/// </summary>
internal sealed class AdaptiveSessionRetentionPolicy
{
    private const int LearnedEvidenceThreshold = 2;
    private const int MaximumProfileCount = 64;

    private static readonly TimeSpan DefaultProbeGracePeriod = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan DefaultMaximumGracePeriod = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultRecentRemovalWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultEvidenceLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ObservedGapMinimumMargin = TimeSpan.FromMilliseconds(250);

    private readonly TimeSpan _evidenceLifetime;
    private readonly TimeSpan _maximumGracePeriod;
    private readonly Lock _stateLock = new();
    private readonly TimeSpan _probeGracePeriod;
    private readonly Dictionary<string, RecreationProfile> _profiles =
        new(StringComparer.Ordinal);

    public AdaptiveSessionRetentionPolicy()
        : this(
            DefaultProbeGracePeriod,
            DefaultMaximumGracePeriod,
            DefaultRecentRemovalWindow,
            DefaultEvidenceLifetime)
    {
    }

    internal AdaptiveSessionRetentionPolicy(
        TimeSpan probeGracePeriod,
        TimeSpan maximumGracePeriod,
        TimeSpan recentRemovalWindow,
        TimeSpan evidenceLifetime)
    {
        ValidatePositive(probeGracePeriod, nameof(probeGracePeriod));
        ValidatePositive(maximumGracePeriod, nameof(maximumGracePeriod));
        ValidatePositive(recentRemovalWindow, nameof(recentRemovalWindow));
        ValidatePositive(evidenceLifetime, nameof(evidenceLifetime));
        if (maximumGracePeriod < probeGracePeriod)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumGracePeriod),
                maximumGracePeriod,
                "The maximum grace period must not be shorter than the probe grace period.");
        }

        this._probeGracePeriod = probeGracePeriod;
        this._maximumGracePeriod = maximumGracePeriod;
        this.RecentRemovalWindow = recentRemovalWindow;
        this._evidenceLifetime = evidenceLifetime;
    }

    public TimeSpan RecentRemovalWindow { get; }

    public TimeSpan GetGracePeriod(
        string applicationId,
        TimeSpan now,
        bool isUnambiguous)
    {
        if (!isUnambiguous || string.IsNullOrWhiteSpace(applicationId))
        {
            return this._probeGracePeriod;
        }

        lock (this._stateLock)
        {
            if (!this.TryGetCurrentProfileUnderLock(applicationId, now, out var profile))
            {
                return this._probeGracePeriod;
            }

            return profile.Evidence >= LearnedEvidenceThreshold
                ? profile.GracePeriod
                : this._probeGracePeriod;
        }
    }

    public bool RecordRecreation(
        string applicationId,
        SessionRecreationEvidence evidence,
        TimeSpan? missingDuration,
        TimeSpan now)
    {
        if (string.IsNullOrWhiteSpace(applicationId))
        {
            return false;
        }

        lock (this._stateLock)
        {
            var wasLearned = this.TryGetCurrentProfileUnderLock(
                applicationId,
                now,
                out var profile) &&
                profile.Evidence >= LearnedEvidenceThreshold;
            var previousGracePeriod = wasLearned
                ? profile.GracePeriod
                : this._probeGracePeriod;
            var evidenceValue = evidence == SessionRecreationEvidence.Strong
                ? LearnedEvidenceThreshold
                : 1;
            var observedGracePeriod = missingDuration is { } duration
                ? this.CalculateObservedGracePeriod(duration)
                : this._probeGracePeriod;
            var updatedEvidence = Math.Min(
                LearnedEvidenceThreshold,
                profile.Evidence + evidenceValue);
            var updatedGracePeriod = profile.GracePeriod > observedGracePeriod
                ? profile.GracePeriod
                : observedGracePeriod;
            this._profiles[applicationId] = new(
                updatedEvidence,
                updatedGracePeriod,
                now);

            if (this._profiles.Count > MaximumProfileCount)
            {
                this.RemoveExpiredProfilesUnderLock(now);
                while (this._profiles.Count > MaximumProfileCount)
                {
                    var oldestApplicationId = this._profiles.MinBy(
                        static profile => profile.Value.LastEvidence).Key;
                    this._profiles.Remove(oldestApplicationId);
                }
            }

            var effectiveGracePeriod = updatedEvidence >= LearnedEvidenceThreshold
                ? updatedGracePeriod
                : this._probeGracePeriod;
            return effectiveGracePeriod > previousGracePeriod;
        }
    }

    private bool TryGetCurrentProfileUnderLock(
        string applicationId,
        TimeSpan now,
        out RecreationProfile profile)
    {
        if (!this._profiles.TryGetValue(applicationId, out profile))
        {
            return false;
        }

        if (now - profile.LastEvidence < this._evidenceLifetime)
        {
            return true;
        }

        this._profiles.Remove(applicationId);
        profile = default;
        return false;
    }

    private void RemoveExpiredProfilesUnderLock(TimeSpan now)
    {
        foreach (var (applicationId, profile) in this._profiles.ToArray())
        {
            if (now - profile.LastEvidence >= this._evidenceLifetime)
            {
                this._profiles.Remove(applicationId);
            }
        }
    }

    private TimeSpan CalculateObservedGracePeriod(TimeSpan missingDuration)
    {
        if (missingDuration < TimeSpan.Zero)
        {
            missingDuration = TimeSpan.Zero;
        }

        var proportionalMargin = TimeSpan.FromTicks(missingDuration.Ticks / 2);
        var margin = proportionalMargin > ObservedGapMinimumMargin
            ? proportionalMargin
            : ObservedGapMinimumMargin;
        var gracePeriod = missingDuration + margin;
        if (gracePeriod < this._probeGracePeriod)
        {
            return this._probeGracePeriod;
        }

        return gracePeriod < this._maximumGracePeriod
            ? gracePeriod
            : this._maximumGracePeriod;
    }

    private static void ValidatePositive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "The duration must be positive.");
        }
    }

    private readonly record struct RecreationProfile(
        int Evidence,
        TimeSpan GracePeriod,
        TimeSpan LastEvidence);
}