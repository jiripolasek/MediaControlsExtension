// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal sealed partial class CoalescingAsyncLoader<TArg, TResult> : IDisposable
    where TResult : class
{
    private readonly Lock _lock = new();
    // Prevent disposal from overtaking a committed result before its callback finishes.
    private readonly Lock _publicationLock = new();
    private readonly Func<TArg, CancellationToken, Task<TResult?>> _loader;
    private readonly Func<TArg, TArg, TArg>? _coalesce;
    private CancellationTokenSource? _activeCts;
    private TResult? _currentResult;
    private TArg _pendingArg = default!;
    private Action<TResult?>? _onResultChanged;
    private Action<TResult?>? _onResultDispose;
    private bool _disposed;
    private bool _hasPending;
    private bool _isRunning;

    public CoalescingAsyncLoader(
        Func<TArg, CancellationToken, Task<TResult?>> loader,
        Action<TResult?> onResultChanged,
        Action<TResult?>? onResultDispose = null,
        Func<TArg, TArg, TArg>? coalesce = null)
    {
        this._loader = loader ?? throw new ArgumentNullException(nameof(loader));
        this._onResultChanged = onResultChanged ?? throw new ArgumentNullException(nameof(onResultChanged));
        this._onResultDispose = onResultDispose;
        this._coalesce = coalesce;
    }

    public TResult? CurrentResult
    {
        get
        {
            lock (this._lock)
            {
                return this._currentResult;
            }
        }
    }

    public void Schedule(TArg arg)
    {
        var startWorker = false;
        lock (this._lock)
        {
            if (this._disposed)
            {
                return;
            }

            this._pendingArg = this._hasPending && this._coalesce is not null
                ? this._coalesce(this._pendingArg, arg)
                : arg;
            this._hasPending = true;
            if (this._coalesce is null)
            {
                this._activeCts?.Cancel();
            }

            if (!this._isRunning)
            {
                this._isRunning = true;
                startWorker = true;
            }
        }

        if (startWorker)
        {
            // A WinRT call can block before it returns an awaitable. Starting the
            // loop on the pool keeps the event callback that scheduled it free.
            _ = Task.Run(this.RunLoaderLoopAsync);
        }
    }

    private async Task RunLoaderLoopAsync()
    {
        while (true)
        {
            TArg arg;
            CancellationTokenSource cts;

            lock (this._lock)
            {
                if (this._disposed || !this._hasPending)
                {
                    this._isRunning = false;
                    return;
                }

                arg = this._pendingArg;
                this._pendingArg = default!;
                this._hasPending = false;
                cts = new CancellationTokenSource();
                this._activeCts = cts;
            }

            TResult? result = null;
            var completed = false;
            try
            {
                result = await this._loader(arg, cts.Token).ConfigureAwait(false);
                completed = true;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }

            lock (this._publicationLock)
            {
                TResult? oldResult = null;
                Action<TResult?>? onResultChanged = null;
                Action<TResult?>? onResultDispose;
                var publishResult = false;
                var disposeResult = false;

                lock (this._lock)
                {
                    if (ReferenceEquals(this._activeCts, cts))
                    {
                        this._activeCts = null;
                    }

                    onResultDispose = this._onResultDispose;
                    if (completed && !this._disposed && !cts.IsCancellationRequested && !this._hasPending)
                    {
                        oldResult = this._currentResult;
                        if (!Equals(result, oldResult))
                        {
                            this._currentResult = result;
                            onResultChanged = this._onResultChanged;
                            publishResult = true;
                        }
                        else if (!ReferenceEquals(result, oldResult))
                        {
                            disposeResult = true;
                        }
                    }
                    else
                    {
                        disposeResult = true;
                    }
                }

                cts.Dispose();

                try
                {
                    if (publishResult)
                    {
                        try
                        {
                            onResultChanged?.Invoke(result);
                        }
                        finally
                        {
                            DisposeResult(oldResult, onResultDispose);
                        }
                    }
                    else if (disposeResult)
                    {
                        DisposeResult(result, onResultDispose);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex);
                }
            }
        }
    }

    public void Dispose()
    {
        TResult? currentResult;
        Action<TResult?>? onResultDispose;

        lock (this._publicationLock)
        {
            lock (this._lock)
            {
                if (this._disposed)
                {
                    return;
                }

                this._disposed = true;
                this._hasPending = false;
                this._pendingArg = default!;
                this._activeCts?.Cancel();

                currentResult = this._currentResult;
                this._currentResult = null;
                onResultDispose = this._onResultDispose;
                this._onResultChanged = null;
                this._onResultDispose = null;
            }

            DisposeResult(currentResult, onResultDispose);
        }
    }

    private static void DisposeResult(TResult? result, Action<TResult?>? onResultDispose)
    {
        if (result is IDisposable disposable)
        {
            disposable.Dispose();
        }

        onResultDispose?.Invoke(result);
    }
}