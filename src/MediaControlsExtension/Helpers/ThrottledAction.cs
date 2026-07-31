// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Timer = System.Timers.Timer;

namespace JPSoftworks.MediaControlsExtension.Pages;

internal sealed partial class ThrottledAction : IDisposable
{
    private readonly Lock _lock = new();
    // Prevent disposal from overtaking an action that has been approved but not started.
    private readonly object _executionLock = new();
    private readonly Func<Task> _action;
    private readonly ILogger _logger;
    private readonly string _operationName;
    private readonly Timer _timer;
    private bool _disposed;
    private bool _isRunning;
    private bool _runPending;

    public ThrottledAction(int interval, string operationName, Action action, ILogger logger)
        : this(interval, operationName, WrapAction(action), logger)
    {
    }

    public ThrottledAction(int interval, string operationName, Func<Task> action, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(logger);

        this._action = action;
        this._logger = logger;
        this._operationName = operationName;
        this._timer = new Timer(interval) { AutoReset = false };
        this._timer.Elapsed += this.TimerOnElapsed;
    }

    public void Invoke()
    {
        lock (this._lock)
        {
            ObjectDisposedException.ThrowIf(this._disposed, this);

            this._timer.Stop();
            this._timer.Start();
        }
    }

    public void Dispose()
    {
        using var diagnostics = new ExtensionOperationDiagnostics(
            $"throttled action disposal {this._operationName}",
            this._logger);
        diagnostics.SetStage("waiting for the execution lock");
        lock (this._executionLock)
        {
            diagnostics.SetStage("waiting for the state lock");
            lock (this._lock)
            {
                if (this._disposed)
                {
                    return;
                }

                this._disposed = true;
                this._runPending = false;
                this._timer.Stop();
                this._timer.Elapsed -= this.TimerOnElapsed;
            }
        }

        diagnostics.SetStage("disposing timer");
        this._timer.Dispose();
        diagnostics.Complete();
    }

    private static Func<Task> WrapAction(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return () =>
        {
            action();
            return Task.CompletedTask;
        };
    }

    private void TimerOnElapsed(object? sender, System.Timers.ElapsedEventArgs args)
    {
        lock (this._lock)
        {
            if (this._disposed)
            {
                return;
            }

            if (this._isRunning)
            {
                this._runPending = true;
                return;
            }

            this._isRunning = true;
        }

        _ = this.RunAsync();
    }

    private async Task RunAsync()
    {
        // Do not execute a potentially blocking action on the timer callback.
        await Task.Yield();

        while (true)
        {
            using var diagnostics = new ExtensionOperationDiagnostics(
                $"throttled action {this._operationName}",
                this._logger);
            var outcome = "completed";
            try
            {
                Task actionTask;
                diagnostics.SetStage("waiting for the execution lock");
                lock (this._executionLock)
                {
                    diagnostics.SetStage("waiting for the state lock");
                    lock (this._lock)
                    {
                        if (this._disposed)
                        {
                            this._isRunning = false;
                            outcome = "stopped after disposal";
                            return;
                        }
                    }

                    diagnostics.SetStage("invoking callback");
                    actionTask = this._action();
                }

                diagnostics.SetStage("awaiting callback completion");
                await actionTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                outcome = "failed";
                ExtensionLog.UnexpectedError(this._logger, ex);
            }

            diagnostics.SetStage("waiting for the state lock after callback");
            var stopAfterCallback = false;
            lock (this._lock)
            {
                if (this._disposed || !this._runPending)
                {
                    this._isRunning = false;
                    stopAfterCallback = true;
                }
                else
                {
                    this._runPending = false;
                }
            }

            diagnostics.Complete(outcome);
            if (stopAfterCallback)
            {
                return;
            }
        }
    }
}
