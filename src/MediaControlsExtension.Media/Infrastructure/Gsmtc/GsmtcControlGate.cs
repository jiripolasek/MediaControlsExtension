// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using JPSoftworks.MediaControlsExtension.Media.Diagnostics;
using Microsoft.Extensions.Logging;

namespace JPSoftworks.MediaControlsExtension.Media.Infrastructure.Gsmtc;

/// <summary>
/// Serializes GSMTC control and lifecycle access. A timed-out native call keeps
/// the lane until it really returns, while callers are released with an open-
/// circuit result instead of accumulating blocked worker threads.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "A timed-out native operation can retain and later release the semaphore after the service has stopped.")]
internal sealed class GsmtcControlGate(ILogger logger)
{
    private static readonly TimeSpan CommandQueueTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ResumeGraceDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SlowOperationThreshold = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _circuitOpenedCts = new();
    private readonly ILogger _logger = logger;
    private long _nextOperationId;
    private int _circuitOpen;

    public bool IsCircuitOpen => Volatile.Read(ref this._circuitOpen) != 0;

    public Task<T> RunAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        return this.RunCoreAsync(
            operation,
            operationName,
            interactive: false,
            allowOpenCircuit: false,
            cancellationToken);
    }

    public Task<T> RunCommandAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        return this.RunCoreAsync(
            operation,
            operationName,
            interactive: true,
            allowOpenCircuit: false,
            cancellationToken);
    }

    public Task<T> RunCleanupAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        return this.RunCoreAsync(
            operation,
            operationName,
            interactive: false,
            allowOpenCircuit: true,
            cancellationToken);
    }

    private async Task<T> RunCoreAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        bool interactive,
        bool allowOpenCircuit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!allowOpenCircuit)
        {
            this.ThrowIfCircuitOpen();
        }

        using var waitCts = allowOpenCircuit
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                this._circuitOpenedCts.Token);
        var waitToken = waitCts?.Token ?? cancellationToken;
        bool entered;
        try
        {
            entered = interactive
                ? await this._gate.WaitAsync(CommandQueueTimeout, waitToken).ConfigureAwait(false)
                : await WaitWithoutTimeoutAsync(this._gate, waitToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (!allowOpenCircuit &&
                  this.IsCircuitOpen &&
                  !cancellationToken.IsCancellationRequested)
        {
            throw new GsmtcControlCircuitOpenException("previous operation", OperationTimeout);
        }
        if (!entered)
        {
            MediaLog.CommandLaneBusy(this._logger, operationName, CommandQueueTimeout);
            throw new GsmtcControlBusyException(operationName, CommandQueueTimeout);
        }

        if (!allowOpenCircuit && this.IsCircuitOpen)
        {
            this._gate.Release();
            throw new GsmtcControlCircuitOpenException(operationName, OperationTimeout);
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
        var timeoutTask = DelayUntilTimeoutAsync(OperationTimeout, timeoutCts.Token);
        if (await Task.WhenAny(nativeTask, timeoutTask).ConfigureAwait(false) == nativeTask ||
            nativeTask.IsCompleted)
        {
            timeoutCts.Cancel();
            return await nativeTask.ConfigureAwait(false);
        }

        if (Interlocked.CompareExchange(ref this._circuitOpen, 1, 0) == 0)
        {
            this._circuitOpenedCts.Cancel();
            MediaLog.ControlCircuitOpened(
                this._logger,
                operationId,
                operationName,
                OperationTimeout);
        }

        _ = ObserveCompletionAsync(nativeTask);
        throw new GsmtcControlCircuitOpenException(operationName, OperationTimeout);
    }

    private static async Task<bool> WaitWithoutTimeoutAsync(
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task DelayUntilTimeoutAsync(
        TimeSpan timeout,
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

        await Task.Delay(ResumeGraceDelay, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ObserveCompletionAsync<T>(Task<T> task)
    {
        try
        {
            _ = await task.ConfigureAwait(false);
        }
        catch
        {
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
                "control operation",
                operationId,
                operationName,
                SlowOperationThreshold);
        }
    }

    private void ThrowIfCircuitOpen()
    {
        if (this.IsCircuitOpen)
        {
            throw new GsmtcControlCircuitOpenException("previous operation", OperationTimeout);
        }
    }
}

internal sealed class GsmtcControlCircuitOpenException(
    string operationName,
    TimeSpan timeout)
    : InvalidOperationException(
        $"GSMTC control is unavailable because {operationName} did not complete within {timeout}.");

internal sealed class GsmtcControlBusyException(
    string operationName,
    TimeSpan timeout)
    : InvalidOperationException(
        $"GSMTC control operation {operationName} could not acquire the lane within {timeout}.");