// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Concurrent;
using System.Diagnostics;

namespace JPSoftworks.MediaControlsExtension;

/// <summary>
/// Tracks an extension operation independently from the GSMTC gates. This
/// catches stalls in command dispatch, presentation callbacks, loader
/// publication, and other managed work before or after a native call.
/// </summary>
internal sealed partial class ExtensionOperationDiagnostics : IDisposable
{
    private const int MaxSnapshotOperations = 16;
    private static readonly TimeSpan EarlyWarningThreshold = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OngoingWarningInterval = TimeSpan.FromSeconds(15);
    private static readonly ConcurrentDictionary<long, ExtensionOperationDiagnostics> ActiveOperations = new();
    private static readonly Lock WatchdogLock = new();
    private static readonly Timer WatchdogTimer = new(
        static _ => RunWatchdogTimerCallback(),
        null,
        Timeout.InfiniteTimeSpan,
        Timeout.InfiniteTimeSpan);
    private static long _nextId;

    private readonly Lock _stateLock = new();
    private readonly ILogger _logger;
    private readonly long _startedTimestamp = Stopwatch.GetTimestamp();
    private long _stageStartedTimestamp = Stopwatch.GetTimestamp();
    private long _nextWarningTimestamp;
    private string _stage = "created";
    private int _stageThreadId = Environment.CurrentManagedThreadId;
    private int _callerTimedOut;
    private int _state;

    public ExtensionOperationDiagnostics(string name, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(logger);

        this._logger = logger;
        this.Id = Interlocked.Increment(ref _nextId);
        this.Name = name;
        this._nextWarningTimestamp =
            this._startedTimestamp + ToStopwatchTicks(EarlyWarningThreshold);
        ActiveOperations.TryAdd(this.Id, this);
        ScheduleWatchdog();
    }

    public long Id { get; }

    public string Name { get; }

    public void SetStage(string stage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);

        lock (this._stateLock)
        {
            if (Volatile.Read(ref this._state) != 0)
            {
                return;
            }

            this._stage = stage;
            this._stageStartedTimestamp = Stopwatch.GetTimestamp();
            this._stageThreadId = Environment.CurrentManagedThreadId;
        }
    }

    public void ReportCallerTimeout(TimeSpan timeout)
    {
        Interlocked.Exchange(ref this._callerTimedOut, 1);
        var snapshot = this.GetSnapshot();
        ExtensionLog.Warning(
            this._logger,
            $"Extension operation #{snapshot.Id} {snapshot.Name} did not return to its caller within {timeout}; " +
            $"current stage: {snapshot.Stage}; stage elapsed: {snapshot.StageElapsed}; " +
            $"managed thread: {snapshot.StageThreadId}; total elapsed: {snapshot.Elapsed}.");
        this.LogActiveOperations($"operation #{this.Id} caller timeout");
    }

    public void Complete(string outcome = "completed")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        if (Interlocked.Exchange(ref this._state, 1) != 0)
        {
            return;
        }

        ActiveOperations.TryRemove(this.Id, out _);
        ScheduleWatchdog();

        var snapshot = this.GetSnapshot();
        if (snapshot.Elapsed >= EarlyWarningThreshold || snapshot.CallerTimedOut)
        {
            ExtensionLog.Warning(
                this._logger,
                $"Extension operation #{snapshot.Id} {snapshot.Name} {outcome} after {snapshot.Elapsed}; " +
                $"final stage: {snapshot.Stage}; stage elapsed: {snapshot.StageElapsed}; " +
                $"caller timed out: {snapshot.CallerTimedOut}.");
        }
    }

    public void Dispose()
    {
        this.Complete();
    }

    private void LogActiveOperations(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var operations = ActiveOperations.Values
            .Select(static operation => operation.GetSnapshot())
            .OrderByDescending(static operation => operation.Elapsed)
            .ToArray();
        if (operations.Length == 0)
        {
            ExtensionLog.Warning(
                this._logger,
                $"No active extension operations were recorded for {reason}.");
            return;
        }

        var displayedOperations = operations
            .Take(MaxSnapshotOperations)
            .Select(static operation =>
                $"#{operation.Id} {operation.Name}: {operation.Stage} " +
                $"for {operation.StageElapsed} on thread {operation.StageThreadId} " +
                $"(total {operation.Elapsed}, caller timed out: {operation.CallerTimedOut})");
        var omittedCount = operations.Length - MaxSnapshotOperations;
        var omittedSuffix = omittedCount > 0 ? $" | +{omittedCount} more" : string.Empty;
        var threadPoolState = GetThreadPoolState();
        ExtensionLog.Warning(
            this._logger,
            $"Active extension operations for {reason} ({operations.Length}; {threadPoolState}): " +
            string.Join(" | ", displayedOperations) + omittedSuffix);
    }

    private OperationSnapshot GetSnapshot()
    {
        lock (this._stateLock)
        {
            return new(
                this.Id,
                this.Name,
                Stopwatch.GetElapsedTime(this._startedTimestamp),
                this._stage,
                Stopwatch.GetElapsedTime(this._stageStartedTimestamp),
                this._stageThreadId,
                Volatile.Read(ref this._callerTimedOut) != 0);
        }
    }

    private static void RunWatchdogTimerCallback()
    {
        try
        {
            WatchActiveOperations();
        }
        catch
        {
            // Diagnostics must never terminate the extension. Do not try to log
            // here because the logging pipeline may be what failed.
        }
    }

    private static void WatchActiveOperations()
    {
        var timestamp = Stopwatch.GetTimestamp();
        foreach (var entry in ActiveOperations)
        {
            entry.Value.WarnIfDue(timestamp);
        }

        ScheduleWatchdog();
    }

    private static void ScheduleWatchdog()
    {
        lock (WatchdogLock)
        {
            var nextWarningTimestamp = long.MaxValue;
            foreach (var entry in ActiveOperations)
            {
                var operation = entry.Value;
                if (Volatile.Read(ref operation._state) == 0)
                {
                    nextWarningTimestamp = Math.Min(
                        nextWarningTimestamp,
                        Interlocked.Read(ref operation._nextWarningTimestamp));
                }
            }

            if (nextWarningTimestamp == long.MaxValue)
            {
                WatchdogTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return;
            }

            var remainingStopwatchTicks = nextWarningTimestamp - Stopwatch.GetTimestamp();
            var dueTime = remainingStopwatchTicks <= 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds((double)remainingStopwatchTicks / Stopwatch.Frequency);
            WatchdogTimer.Change(dueTime, Timeout.InfiniteTimeSpan);
        }
    }

    private static long ToStopwatchTicks(TimeSpan duration)
    {
        return checked(duration.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond);
    }

    private void WarnIfDue(long timestamp)
    {
        if (Volatile.Read(ref this._state) != 0)
        {
            return;
        }

        var nextWarningTimestamp = Interlocked.Read(ref this._nextWarningTimestamp);
        if (timestamp < nextWarningTimestamp ||
            Interlocked.CompareExchange(
                ref this._nextWarningTimestamp,
                timestamp + ToStopwatchTicks(OngoingWarningInterval),
                nextWarningTimestamp) != nextWarningTimestamp)
        {
            return;
        }

        var snapshot = this.GetSnapshot();
        ExtensionLog.Warning(
            this._logger,
            $"Extension operation #{snapshot.Id} {snapshot.Name} has not completed after {snapshot.Elapsed}; " +
            $"current stage: {snapshot.Stage}; stage elapsed: {snapshot.StageElapsed}; " +
            $"managed thread: {snapshot.StageThreadId}; caller timed out: {snapshot.CallerTimedOut}; " +
            $"active operations: {ActiveOperations.Count}; {GetThreadPoolState()}.");
    }

    private static string GetThreadPoolState()
    {
        ThreadPool.GetAvailableThreads(out var availableWorkerThreads, out var availableCompletionPortThreads);
        ThreadPool.GetMaxThreads(out var maximumWorkerThreads, out var maximumCompletionPortThreads);
        return $"thread pool: {ThreadPool.ThreadCount} threads, {ThreadPool.PendingWorkItemCount} pending, " +
               $"workers {availableWorkerThreads}/{maximumWorkerThreads} available, " +
               $"I/O {availableCompletionPortThreads}/{maximumCompletionPortThreads} available";
    }

    private readonly record struct OperationSnapshot(
        long Id,
        string Name,
        TimeSpan Elapsed,
        string Stage,
        TimeSpan StageElapsed,
        int StageThreadId,
        bool CallerTimedOut);
}