// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Diagnostics;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Observation = JPSoftworks.MediaControlsExtension.Media.Gsmtc.GsmtcPlaybackController.PlaybackObservation;

namespace JPSoftworks.MediaControlsExtension.Media.Gsmtc;

/// <summary>Shares versioned scalar playback observations for one binding and its waiters.</summary>
internal sealed class GsmtcPlaybackObservations
{
    private static readonly TimeSpan DefaultSnapshotWaitTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultResumeGraceDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SlowReadThreshold = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StalledReadThreshold = TimeSpan.FromSeconds(3);

    private readonly Func<TimeSpan, TimeSpan, CancellationToken, Task> _delayUntilTimeout
        = GsmtcUnbiasedClock.DelayUntilTimeoutAsync;

    private readonly ILogger _logger;
    private readonly Action? _onPublished;
    private readonly string _operationName;
    private readonly TimeSpan _resumeGraceDelay = DefaultResumeGraceDelay;
    private readonly TaskCompletionSource _retiredSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan _snapshotWaitTimeout = DefaultSnapshotWaitTimeout;
    private readonly Lock _stateLock;
    private long _attemptedVersion;
    private long _changeVersion = 1;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private object? _commandReadClaim;
    private Sample _latest;
    private long _nextSequence;
    private ReadFlight? _read;
    private bool _retired;

    public bool IsSnapshotSuspended
    {
        get
        {
            lock (this._stateLock)
            {
                return this._read is { Task.IsCompleted: false, SnapshotWaitExpired: true };
            }
        }
    }

    public bool IsDirty
    {
        get
        {
            lock (this._stateLock)
            {
                return this._latest.ChangeVersion < this._changeVersion;
            }
        }
    }

    public Sample Latest
    {
        get
        {
            lock (this._stateLock)
            {
                return this._latest;
            }
        }
    }

    public bool NeedsSnapshotRead
    {
        get
        {
            lock (this._stateLock)
            {
                if (this._retired || this._commandReadClaim is not null || !this.IsDirty)
                {
                    return false;
                }

                return this._read is null ||
                       (this._read.Task.IsCompleted
                           ? this._attemptedVersion < this._changeVersion
                           : !this._read.SnapshotWaitExpired);
            }
        }
    }

    internal GsmtcPlaybackObservations(
        Lock stateLock,
        TimeSpan snapshotWaitTimeout,
        TimeSpan resumeGraceDelay,
        Action? onPublished = null,
        ILogger? logger = null,
        Func<TimeSpan, TimeSpan, CancellationToken, Task>? delayUntilTimeout = null)
        : this(stateLock, onPublished, logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(snapshotWaitTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(resumeGraceDelay, TimeSpan.Zero);
        this._snapshotWaitTimeout = snapshotWaitTimeout;
        this._resumeGraceDelay = resumeGraceDelay;
        this._delayUntilTimeout = delayUntilTimeout ?? GsmtcUnbiasedClock.DelayUntilTimeoutAsync;
    }

    public GsmtcPlaybackObservations(
        Lock stateLock,
        Action? onPublished = null,
        ILogger? logger = null,
        string operationName = "GetPlaybackInfo")
    {
        this._stateLock = stateLock;
        this._onPublished = onPublished;
        this._logger = logger ?? NullLogger.Instance;
        this._operationName = operationName;
    }

    public void EnsureActive()
    {
        lock (this._stateLock)
        {
            this.ThrowIfRetired();
        }
    }

    public void Invalidate()
    {
        lock (this._stateLock)
        {
            if (!this._retired)
            {
                this._changeVersion++;
                this.Pulse();
            }
        }
    }

    public void Retire()
    {
        lock (this._stateLock)
        {
            this._retired = true;
            this._retiredSignal.TrySetResult();
            this.Pulse();
        }
    }

    public long CaptureSequence()
    {
        lock (this._stateLock)
        {
            this.ThrowIfRetired();

            // Include outstanding reads so none can confirm a later send decision.
            return this._nextSequence;
        }
    }

    public MediaBackendSessionSnapshot Merge(MediaBackendSessionSnapshot snapshot, MediaCapabilities sourceCapabilities)
    {
        lock (this._stateLock)
        {
            var capabilities = this._latest.Value.Capabilities | sourceCapabilities;
            return this._latest.Sequence == 0 ||
                   snapshot.PlaybackState == this._latest.Value.State && snapshot.Capabilities == capabilities
                ? snapshot
                : snapshot with { PlaybackState = this._latest.Value.State, Capabilities = capabilities, };
        }
    }

    public Sample ReadCommand(Func<Observation> read)
    {
        Sample ticket;
        object? claim;
        lock (this._stateLock)
        {
            ticket = this.BeginRead();
            claim = this._commandReadClaim;
        }

        try
        {
            return this.Publish(ticket with { Value = read() });
        }
        finally
        {
            this.ReleaseCommandRead(claim);
        }
    }

    public async Task<T> WithCommandReadAsync<T>(Func<Task<T>> command, CancellationToken cancellationToken)
    {
        object claim;
        while (true)
        {
            Task pending;
            lock (this._stateLock)
            {
                this.ThrowIfRetired();
                cancellationToken.ThrowIfCancellationRequested();
                if (this._read is { Task.IsCompleted: false })
                {
                    pending = this._read.Task;
                }
                else if (this._commandReadClaim is not null)
                {
                    pending = this._changed.Task;
                }
                else
                {
                    // Reserve before waiting for the control gate so snapshots cannot overtake it.
                    this._commandReadClaim = claim = new();
                    break;
                }
            }

            await this.AwaitReadCompletionAsync(pending, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await command().ConfigureAwait(false);
        }
        finally
        {
            this.ReleaseCommandRead(claim);
        }
    }

    private void ReleaseCommandRead(object? claim)
    {
        var signal = false;
        lock (this._stateLock)
        {
            if (claim is not null && ReferenceEquals(this._commandReadClaim, claim))
            {
                this._commandReadClaim = null;
                this.Pulse();
                signal = !this._retired && this.IsDirty;
            }
        }

        if (signal)
        {
            this._onPublished?.Invoke();
        }
    }

    public async Task DrainPendingReadAsync(CancellationToken cancellationToken)
    {
        Task<Sample>? pending;
        lock (this._stateLock)
        {
            this.ThrowIfRetired();
            pending = this._read is { Task.IsCompleted: false } ? this._read.Task : null;
        }

        if (pending is not null)
        {
            // Drain ownership, not the preceding snapshot's outcome.
            await this.AwaitReadCompletionAsync(pending, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<Sample> ReadSnapshotAsync(Func<Task<Observation>> read, CancellationToken cancellationToken)
    {
        ReadFlight pending;
        lock (this._stateLock)
        {
            this.ThrowIfRetired();
            cancellationToken.ThrowIfCancellationRequested();
            if (!this.NeedsSnapshotRead)
            {
                return this._latest;
            }

            pending = this.GetOrStartRead(read, signalPublication: false);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var live = this.AwaitLiveAsync(pending.Task, budget.Token);
        try
        {
            var timeout = this._delayUntilTimeout(this._snapshotWaitTimeout, this._resumeGraceDelay, budget.Token);
            await Task.WhenAny(live, timeout).WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            this.EnsureActive();
            if (live.IsCompleted || pending.Task.IsCompleted)
            {
                return await this.AwaitLiveAsync(pending.Task, cancellationToken).ConfigureAwait(false);
            }

            this.AbandonSnapshot(pending, timedOut: true);
            throw new TimeoutException("The shared playback read exceeded the snapshot wait budget and resume grace.");
        }
        catch (OperationCanceledException)
        {
            this.AbandonSnapshot(pending, timedOut: false);
            throw;
        }
        finally
        {
            budget.Cancel();
            await ((Task)live).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public async Task<Sample> ReadFreshAsync(
        Func<Task<Observation>> read,
        long afterSequence,
        long notBefore,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), notBefore);
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
        }

        while (true)
        {
            Task<Sample>? task;
            Task changed;
            lock (this._stateLock)
            {
                this.ThrowIfRetired();
                cancellationToken.ThrowIfCancellationRequested();
                if (this._latest.Sequence > afterSequence && this._latest.StartedAt >= notBefore)
                {
                    return this._latest;
                }

                changed = this._changed.Task;
                task = this._commandReadClaim is not null
                    ? null
                    : (this._read is { Task.IsCompleted: false } pending
                        ? pending
                        : this.StartRead(read, signalPublication: true)).Task;
            }

            if (task is null)
            {
                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            var sample = await this.AwaitLiveAsync(task, cancellationToken).ConfigureAwait(false);
            if (sample.Sequence > afterSequence && sample.StartedAt >= notBefore)
            {
                return sample;
            }
        }
    }

    public async Task<Sample> WaitForObservationAsync(
        long afterSequence,
        Func<Task<Observation>> read,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            Task<Sample>? pending;
            lock (this._stateLock)
            {
                this.ThrowIfRetired();
                cancellationToken.ThrowIfCancellationRequested();
                if (this._latest.Sequence > afterSequence)
                {
                    return this._latest;
                }

                // Register the wakeup under the same lock as the version check.
                changed = this._changed.Task;
                pending = this._commandReadClaim is null && this.IsDirty &&
                          (this._attemptedVersion < this._changeVersion || this._read is { Task.IsCompleted: false })
                    ? this.GetOrStartRead(read, signalPublication: true).Task
                    : null;
            }

            if (pending is not null)
            {
                try
                {
                    var sample = await this.AwaitLiveAsync(pending, cancellationToken).ConfigureAwait(false);
                    if (sample.Sequence > afterSequence)
                    {
                        return sample;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not GsmtcSessionRetiredException &&
                                           !GsmtcErrors.IndicatesStaleSession(ex))
                {
                    // A failed event is consumed; an unused fallback or a newer event can still recover.
                }

                continue;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private ReadFlight GetOrStartRead(Func<Task<Observation>> read, bool signalPublication)
    {
        if (this._read is { Task.IsCompleted: false } ||
            this._attemptedVersion == this._changeVersion && this._read is not null)
        {
            return this._read;
        }

        return this.StartRead(read, signalPublication);
    }

    private ReadFlight StartRead(Func<Task<Observation>> read, bool signalPublication)
    {
        // Keep the claim until the actual read returns, even if every waiter cancels.
        var ticket = this.BeginRead();
        this._attemptedVersion = ticket.ChangeVersion;
        var flight = new ReadFlight(ticket, signalPublication);
        this._read = flight;

        flight.Task = Task.Run(async () =>
        {
            var sample = ticket with { Value = await read().ConfigureAwait(false) };
            return this.Publish(sample, flight);
        });

        _ = flight.Task.ContinueWith(task =>
            {
                _ = task.Exception;
                this.CompleteRead(flight);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        _ = this.MonitorReadAsync(flight);
        return flight;
    }

    private async Task MonitorReadAsync(ReadFlight flight)
    {
        using var cancellation = new CancellationTokenSource();
        try
        {
            var slow = this._delayUntilTimeout(SlowReadThreshold, TimeSpan.Zero, cancellation.Token);
            var stalled = this._delayUntilTimeout(StalledReadThreshold, TimeSpan.Zero, cancellation.Token);
            await Task.WhenAny(flight.Task, slow).ConfigureAwait(false);
            if (flight.Task.IsCompleted)
            {
                return;
            }

            GsmtcLog.NativeOperationSlow(
                this._logger,
                "observation",
                flight.Ticket.Sequence,
                this._operationName,
                SlowReadThreshold);

            await Task.WhenAny(flight.Task, stalled).ConfigureAwait(false);
            if (flight.Task.IsCompleted)
            {
                return;
            }

            this.AbandonSnapshot(flight, timedOut: true);
            GsmtcLog.ObservationsPaused(this._logger, flight.Ticket.Sequence, this._operationName, StalledReadThreshold);
            await ((Task)flight.Task).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            GsmtcLog.ObservationsResumed(this._logger, flight.Ticket.Sequence, this._operationName);
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    private Sample BeginRead()
    {
        this.ThrowIfRetired();
        return new(default, ++this._nextSequence, this._changeVersion, Stopwatch.GetTimestamp(), 0, 0, 0);
    }

    private Sample Publish(Sample sample, ReadFlight? flight = null)
    {
        var signal = false;
        lock (this._stateLock)
        {
            this.ThrowIfRetired();
            sample = sample with { CompletedAt = Stopwatch.GetTimestamp() };
            // Command reads can overtake a snapshot; only the newest read can replace scalars.
            if (sample.Sequence > this._latest.Sequence)
            {
                sample = sample with
                {
                    // Preserve resets even when a waiter skips an intermediate publication.
                    StoppedRun = sample.Value.State == MediaPlaybackState.Stopped
                        ? this._latest.StoppedRun != 0 ? this._latest.StoppedRun : sample.Sequence
                        : 0,
                    StopOnlyRun = GsmtcPlaybackController.IsStopOnlyPause(MediaOperation.Pause, sample.Value)
                        ? this._latest.StopOnlyRun != 0 ? this._latest.StopOnlyRun : sample.Sequence
                        : 0,
                };
                this._latest = sample;
                if (flight is null)
                {
                    signal = true;
                }

                this.Pulse();
            }
        }

        if (signal)
        {
            this._onPublished?.Invoke();
        }

        return sample;
    }

    private void AbandonSnapshot(ReadFlight flight, bool timedOut)
    {
        bool signal;
        lock (this._stateLock)
        {
            flight.SnapshotWaitExpired |= timedOut;
            flight.SignalPublication = true;
            signal = this.TrySignalPublication(flight);
        }

        if (signal)
        {
            this._onPublished?.Invoke();
        }
    }

    private bool TrySignalPublication(ReadFlight flight)
    {
        if (!this._retired && flight.Task.IsCompletedSuccessfully && flight.SignalPublication &&
            !flight.PublicationSignaled &&
            this._latest.Sequence == flight.Ticket.Sequence)
        {
            flight.PublicationSignaled = true;
            return true;
        }

        return false;
    }

    private void CompleteRead(ReadFlight flight)
    {
        bool signal;
        lock (this._stateLock)
        {
            // Signal after completion so a skipped newer event can start its read immediately.
            signal = this.TrySignalPublication(flight) ||
                     !this._retired && ReferenceEquals(this._read, flight) && flight is { SnapshotWaitExpired: true, PublicationSignaled: false };
        }

        if (signal)
        {
            this._onPublished?.Invoke();
        }
    }

    private async Task AwaitReadCompletionAsync(Task task, CancellationToken cancellationToken)
    {
        await Task.WhenAny(task, this._retiredSignal.Task).WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (this._stateLock)
        {
            this.ThrowIfRetired();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private async Task<Sample> AwaitLiveAsync(Task<Sample> task, CancellationToken cancellationToken)
    {
        await this.AwaitReadCompletionAsync(task, cancellationToken).ConfigureAwait(false);
        var sample = await task.ConfigureAwait(false);
        lock (this._stateLock)
        {
            this.ThrowIfRetired();
            cancellationToken.ThrowIfCancellationRequested();
            return this._latest.Sequence > sample.Sequence ? this._latest : sample;
        }
    }

    private void ThrowIfRetired()
    {
        if (this._retired)
        {
            throw new GsmtcSessionRetiredException();
        }
    }

    private void Pulse()
    {
        var changed = this._changed;
        this._changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        changed.TrySetResult();
    }

    private sealed class ReadFlight(Sample ticket, bool signalPublication)
    {
        public Sample Ticket { get; } = ticket;
        public Task<Sample> Task { get; set; } = null!;
        public bool SignalPublication { get; set; } = signalPublication;
        public bool PublicationSignaled { get; set; }
        public bool SnapshotWaitExpired { get; set; }
    }

    internal readonly record struct Sample(
        Observation Value,
        long Sequence,
        long ChangeVersion,
        long StartedAt,
        long CompletedAt,
        long StoppedRun,
        long StopOnlyRun);
}