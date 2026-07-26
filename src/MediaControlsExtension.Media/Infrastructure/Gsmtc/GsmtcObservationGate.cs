// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Diagnostics.CodeAnalysis;
using JPSoftworks.MediaControlsExtension.Media.Diagnostics;
using Microsoft.Extensions.Logging;

namespace JPSoftworks.MediaControlsExtension.Media.Infrastructure.Gsmtc;

/// <summary>
/// Isolates GSMTC observations from the control lane. At most one native
/// observation remains outstanding after a timeout.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Timed-out native observations and released waiters can outlive their semaphore and block-generation token.")]
internal sealed class GsmtcObservationGate(ILogger logger)
{
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultResumeGraceDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SlowOperationThreshold = TimeSpan.FromSeconds(2);

    private CancellationTokenSource _blockedCts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger = logger;
    private readonly TimeSpan _operationTimeout = DefaultOperationTimeout;
    private readonly TimeSpan _resumeGraceDelay = DefaultResumeGraceDelay;
    private long _blockingOperationId;
    private long _nextOperationId;
    private int _blocked;

    internal GsmtcObservationGate(
        ILogger logger,
        TimeSpan operationTimeout,
        TimeSpan resumeGraceDelay)
        : this(logger)
    {
        if (operationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(operationTimeout),
                operationTimeout,
                "The observation timeout must be positive.");
        }

        if (resumeGraceDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resumeGraceDelay),
                resumeGraceDelay,
                "The resume grace delay cannot be negative.");
        }

        this._operationTimeout = operationTimeout;
        this._resumeGraceDelay = resumeGraceDelay;
    }

    public bool IsBlocked => Volatile.Read(ref this._blocked) != 0;

    public async Task<T> RunAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var blockedCts = Volatile.Read(ref this._blockedCts);
        if (this.IsBlocked || blockedCts.IsCancellationRequested)
        {
            throw new GsmtcObservationBlockedException(operationName, this._operationTimeout);
        }

        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            blockedCts.Token);
        try
        {
            await this._gate.WaitAsync(waitCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (blockedCts.IsCancellationRequested &&
                  !cancellationToken.IsCancellationRequested)
        {
            throw new GsmtcObservationBlockedException(operationName, this._operationTimeout);
        }

        if (this.IsBlocked || blockedCts.IsCancellationRequested)
        {
            this._gate.Release();
            throw new GsmtcObservationBlockedException(operationName, this._operationTimeout);
        }

        var operationId = Interlocked.Increment(ref this._nextOperationId);
        var nativeTask = Task.Run(
            async () =>
            {
                try
                {
                    return await operation().ConfigureAwait(false);
                }
                finally
                {
                    this._gate.Release();
                }
            });
        _ = this.WarnIfSlowAsync(nativeTask, operationId, operationName);

        using var timeoutCts = new CancellationTokenSource();
        var timeoutTask = DelayUntilTimeoutAsync(
            this._operationTimeout,
            this._resumeGraceDelay,
            timeoutCts.Token);
        if (await Task.WhenAny(nativeTask, timeoutTask).ConfigureAwait(false) == nativeTask ||
            nativeTask.IsCompleted)
        {
            timeoutCts.Cancel();
            return await nativeTask.ConfigureAwait(false);
        }

        this._blockingOperationId = operationId;
        Volatile.Write(ref this._blocked, 1);
        MediaLog.ObservationsPaused(
            this._logger,
            operationId,
            operationName,
            this._operationTimeout);
        blockedCts.Cancel();
        _ = this.RecoverWhenCompleteAsync(nativeTask, operationId, operationName);
        throw new GsmtcObservationBlockedException(operationName, this._operationTimeout);
    }

    private static async Task DelayUntilTimeoutAsync(
        TimeSpan timeout,
        TimeSpan resumeGraceDelay,
        CancellationToken cancellationToken)
    {
        var started = GsmtcUnbiasedClock.GetTime();
        while (true)
        {
            var remaining = timeout - (GsmtcUnbiasedClock.GetTime() - started);
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
        }

        await Task.Delay(resumeGraceDelay, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecoverWhenCompleteAsync<T>(
        Task<T> nativeTask,
        long operationId,
        string operationName)
    {
        try
        {
            _ = await nativeTask.ConfigureAwait(false);
        }
        catch
        {
        }

        if (Interlocked.Read(ref this._blockingOperationId) == operationId)
        {
            _ = Interlocked.Exchange(
                ref this._blockedCts,
                new CancellationTokenSource());
            Interlocked.Exchange(ref this._blockingOperationId, 0);
            Volatile.Write(ref this._blocked, 0);
            MediaLog.ObservationsResumed(this._logger, operationId, operationName);
        }
    }

    private async Task WarnIfSlowAsync<T>(
        Task<T> nativeTask,
        long operationId,
        string operationName)
    {
        using var warningCts = new CancellationTokenSource();
        var warningTask = Task.Delay(SlowOperationThreshold, warningCts.Token);
        if (await Task.WhenAny(nativeTask, warningTask).ConfigureAwait(false) == nativeTask ||
            nativeTask.IsCompleted)
        {
            warningCts.Cancel();
        }
        else
        {
            MediaLog.NativeOperationSlow(
                this._logger,
                "observation",
                operationId,
                operationName,
                SlowOperationThreshold);
        }
    }
}

internal sealed class GsmtcObservationBlockedException(
    string operationName,
    TimeSpan timeout)
    : InvalidOperationException(
        $"GSMTC observation {operationName} did not complete within {timeout}.");