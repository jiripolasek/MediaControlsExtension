// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Timer = System.Timers.Timer;

namespace JPSoftworks.MediaControlsExtension.Pages;

internal sealed partial class ThrottledAction : IDisposable
{
    private readonly Timer _timer;

    public ThrottledAction(int interval, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        this._timer = new Timer(interval) { AutoReset = false };
        this._timer.Elapsed += (_, _) => action.Invoke();
    }

    public void Invoke()
    {
        this._timer.Stop();
        this._timer.Start();
    }

    public void Dispose()
    {
        this._timer.Dispose();
    }
}