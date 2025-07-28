// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal sealed partial class CoalescingAsyncLoader<TArg, TResult> : IDisposable
    where TResult : class
{
    private int _version;
    private CancellationTokenSource? _cts;
    private TResult? _currentResult;
    private readonly Func<TArg, CancellationToken, Task<TResult?>> _loader;
    private Action<TResult?>? _onResultChanged;
    private Action<TResult?>? _onResultDispose;

    public CoalescingAsyncLoader(
        Func<TArg, CancellationToken, Task<TResult?>> loader,
        Action<TResult?> onResultChanged,
        Action<TResult?>? onResultDispose = null)
    {
        this._loader = loader ?? throw new ArgumentNullException(nameof(loader));
        this._onResultChanged = onResultChanged ?? throw new ArgumentNullException(nameof(onResultChanged));
        this._onResultDispose = onResultDispose;
    }

    public TResult? CurrentResult => this._currentResult;

    public void Schedule(TArg arg)
    {
        this._cts?.Cancel();
        this._cts?.Dispose();
        this._cts = new CancellationTokenSource();

        int myVersion = ++this._version;
        var ct = this._cts.Token;

        _ = this.RunLoaderAsync(arg, myVersion, ct);
    }

    private async Task RunLoaderAsync(TArg arg, int version, CancellationToken ct)
    {
        TResult? result = null;
        try
        {
            result = await this._loader(arg, ct);
            if (version != this._version)
            {
                this.DisposeResult(result);
                return;
            }

            var old = this._currentResult;
            if (!Equals(result, old))
            {
                this._currentResult = result;
                this._onResultChanged?.Invoke(result);
                this.DisposeResult(old);
            }
            else
            {
                this.DisposeResult(result);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            this.DisposeResult(result);
            System.Diagnostics.Debug.WriteLine($"Error in CoalescingAsyncLoader: {ex}");
        }
    }

    private void DisposeResult(TResult? result)
    {
        if (result is IDisposable d)
        {
            d.Dispose();
        }

        this._onResultDispose?.Invoke(result);
    }

    public void Dispose()
    {
        this._cts?.Cancel();
        this._cts?.Dispose();
        this.DisposeResult(this._currentResult);
    
        this._onResultChanged = null; // or make it nullable and set to null
        this._onResultDispose = null;
    }
}