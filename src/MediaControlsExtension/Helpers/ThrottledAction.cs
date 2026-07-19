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
    private readonly Timer _timer;
    private bool _disposed;
    private bool _isRunning;
    private bool _runPending;

    public ThrottledAction(int interval, Action action)
        : this(interval, WrapAction(action))
    {
    }

    public ThrottledAction(int interval, Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        this._action = action;
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
        lock (this._executionLock)
        {
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

        this._timer.Dispose();
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
            try
            {
                Task actionTask;
                lock (this._executionLock)
                {
                    lock (this._lock)
                    {
                        if (this._disposed)
                        {
                            this._isRunning = false;
                            return;
                        }
                    }

                    actionTask = this._action();
                }

                await actionTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }

            lock (this._lock)
            {
                if (this._disposed || !this._runPending)
                {
                    this._isRunning = false;
                    return;
                }

                this._runPending = false;
            }
        }
    }
}