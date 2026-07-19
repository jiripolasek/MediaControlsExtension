// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Runtime.InteropServices;

namespace JPSoftworks.MediaControlsExtension.Threading;

/// <summary>
/// Serializes access to Global System Media Transport Controls. A native GSMTC
/// call can block before returning its asynchronous operation, so allowing only
/// one caller into the API prevents a stalled call from becoming a thread
/// stampede.
/// </summary>
internal static partial class GsmtcOperationGate
{
    // Deliberately longer than AsyncInvokableCommand.Timeout: commands own the UX latency,
    // so the watchdog only needs to catch a genuinely wedged native call.
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);

    // Armed after the unbiased deadline passes, while the watchdog is provably
    // running; gives an operation that thawed together with us time to complete.
    private static readonly TimeSpan ResumeGraceDelay = TimeSpan.FromSeconds(2);
    private static readonly AsyncLocal<GateLease?> CurrentLease = new();
    private static readonly CancellationTokenSource CircuitOpenedCts = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Lock CircuitLock = new();
    private static int _isCircuitOpen;
    private static string? _blockingOperationName;

    public static bool IsCircuitOpen => Volatile.Read(ref _isCircuitOpen) != 0;

    internal static Task RunDetached(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var inheritedLease = CurrentLease.Value;
        // Task.Run captures ExecutionContext synchronously; hide only this gate's lease.
        CurrentLease.Value = null;
        try
        {
            return Task.Run(operation);
        }
        finally
        {
            CurrentLease.Value = inheritedLease;
        }
    }

    public static async Task RunAsync(
        Action operation,
        CancellationToken cancellationToken = default,
        [System.Runtime.CompilerServices.CallerMemberName] string operationName = "")
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfGateAlreadyHeld();

        await WaitForGateAsync(cancellationToken).ConfigureAwait(false);
        var previousLease = CurrentLease.Value;
        var lease = new GateLease();
        CurrentLease.Value = lease;
        using var watchdog = new OperationWatchdog(operationName);
        try
        {
            operation();
        }
        finally
        {
            lease.Revoke();
            CurrentLease.Value = previousLease;
            watchdog.Complete();
            Gate.Release();
        }
    }

    public static async Task<T> RunAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken = default,
        [System.Runtime.CompilerServices.CallerMemberName] string operationName = "")
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfGateAlreadyHeld();

        await WaitForGateAsync(cancellationToken).ConfigureAwait(false);
        var previousLease = CurrentLease.Value;
        var lease = new GateLease();
        CurrentLease.Value = lease;
        using var watchdog = new OperationWatchdog(operationName);
        try
        {
            return operation();
        }
        finally
        {
            lease.Revoke();
            CurrentLease.Value = previousLease;
            watchdog.Complete();
            Gate.Release();
        }
    }

    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default,
        [System.Runtime.CompilerServices.CallerMemberName] string operationName = "")
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfGateAlreadyHeld();

        await WaitForGateAsync(cancellationToken).ConfigureAwait(false);
        var previousLease = CurrentLease.Value;
        var lease = new GateLease();
        CurrentLease.Value = lease;
        using var watchdog = new OperationWatchdog(operationName);
        try
        {
            // Cancellation only abandons a caller that is still waiting for the
            // gate. Once native work starts, it must retain the gate until it
            // really returns.
            return await operation(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lease.Revoke();
            CurrentLease.Value = previousLease;
            watchdog.Complete();
            Gate.Release();
        }
    }

    public static async Task RunAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default,
        [System.Runtime.CompilerServices.CallerMemberName] string operationName = "")
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfGateAlreadyHeld();

        await WaitForGateAsync(cancellationToken).ConfigureAwait(false);
        var previousLease = CurrentLease.Value;
        var lease = new GateLease();
        CurrentLease.Value = lease;
        using var watchdog = new OperationWatchdog(operationName);
        try
        {
            await operation(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lease.Revoke();
            CurrentLease.Value = previousLease;
            watchdog.Complete();
            Gate.Release();
        }
    }

    private static async Task WaitForGateAsync(CancellationToken cancellationToken)
    {
        ThrowIfCircuitOpen();

        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            CircuitOpenedCts.Token);
        try
        {
            await Gate.WaitAsync(waitCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (IsCircuitOpen && !cancellationToken.IsCancellationRequested)
        {
            throw CreateCircuitOpenException();
        }

        if (IsCircuitOpen)
        {
            Gate.Release();
            throw CreateCircuitOpenException();
        }
    }

    internal static void VerifyAccess()
    {
        if (CurrentLease.Value is not { IsActive: true })
        {
            throw new InvalidOperationException("GSMTC was accessed outside GsmtcOperationGate.");
        }
    }

    private static void ThrowIfGateAlreadyHeld()
    {
        if (CurrentLease.Value is { IsActive: true })
        {
            throw new InvalidOperationException("Nested GsmtcOperationGate.RunAsync calls are not supported.");
        }
    }

    private static partial class UnbiasedClock
    {
        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool QueryUnbiasedInterruptTime(out ulong unbiasedInterruptTime);

        /// <summary>
        /// Monotonic time that excludes periods the system was suspended, unlike
        /// the tick count <see cref="Task.Delay(TimeSpan)"/> schedules against.
        /// </summary>
        public static TimeSpan GetTime()
        {
            // 100 ns units; falls back to the biased clock if the call ever fails,
            // which degrades to the previous suspend-sensitive behavior.
            return QueryUnbiasedInterruptTime(out var interruptTime)
                ? TimeSpan.FromTicks((long)interruptTime)
                : TimeSpan.FromMilliseconds(Environment.TickCount64);
        }
    }

    private sealed class GateLease
    {
        private int _isActive = 1;

        public bool IsActive => Volatile.Read(ref this._isActive) != 0;

        public void Revoke()
        {
            Interlocked.Exchange(ref this._isActive, 0);
        }
    }

    private sealed partial class OperationWatchdog : IDisposable
    {
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly string _operationName;
        private int _state;

        public OperationWatchdog(string operationName)
        {
            this._operationName = operationName;
            _ = this.WatchAsync();
        }

        public void Complete()
        {
            if (Interlocked.CompareExchange(ref this._state, 1, 0) == 0)
            {
                this._cancellationTokenSource.Cancel();
            }
        }

        public void Dispose()
        {
            this.Complete();
            this._cancellationTokenSource.Dispose();
        }

        private async Task WatchAsync()
        {
            var cancellationToken = this._cancellationTokenSource.Token;
            try
            {
                // Task.Delay schedules against a clock that keeps counting while the
                // system is suspended, so a sleep/resume cycle can exhaust the delay
                // without the operation ever getting to run. Track the deadline in
                // unbiased time (excludes suspend) and re-arm until it truly passes.
                var start = UnbiasedClock.GetTime();
                while (true)
                {
                    var remaining = OperationTimeout - (UnbiasedClock.GetTime() - start);
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                }

                // Unbiased time still advances while Modern Standby freezes the whole
                // process, so the expired deadline may be freeze debt rather than a
                // wedged call. This delay is armed while we are provably running
                // again; a healthy operation that thawed with us completes during it.
                await Task.Delay(ResumeGraceDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref this._state, 2, 0) == 0)
            {
                OpenCircuit(this._operationName);
            }
        }
    }

    private static void OpenCircuit(string operationName)
    {
        lock (CircuitLock)
        {
            if (IsCircuitOpen)
            {
                return;
            }

            _blockingOperationName = operationName;
            Volatile.Write(ref _isCircuitOpen, 1);
        }

        CircuitOpenedCts.Cancel();
        Logger.LogError($"GSMTC circuit opened because {operationName} did not complete within {OperationTimeout}. Restart the extension to retry.");
    }

    private static void ThrowIfCircuitOpen()
    {
        if (IsCircuitOpen)
        {
            throw CreateCircuitOpenException();
        }
    }

    private static GsmtcCircuitOpenException CreateCircuitOpenException()
    {
        return new(_blockingOperationName, OperationTimeout);
    }
}

internal sealed class GsmtcCircuitOpenException(
    string? blockingOperationName,
    TimeSpan operationTimeout)
    : InvalidOperationException(
        $"GSMTC is unavailable because {blockingOperationName ?? "an operation"} did not complete within {operationTimeout}. Restart the extension to retry.");