// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Diagnostics;

namespace JPSoftworks.MediaControlsExtension.Media.ITunes;

internal enum ITunesEventAction
{
    Ignore,
    Refresh,
    Disconnect,
}

/// <summary>Classifies the DISPIDs exposed by <c>_IiTunesEvents</c>.</summary>
internal static class ITunesEventClassifier
{
    public static ITunesEventAction Classify(int dispId) => (ITunesEventDispId)dispId switch
    {
        ITunesEventDispId.AboutToPromptUserToQuit or ITunesEventDispId.Quitting => ITunesEventAction.Disconnect,
        ITunesEventDispId.DatabaseChanged or
        ITunesEventDispId.PlayerPlay or
        ITunesEventDispId.PlayerStop or
        ITunesEventDispId.PlayerPlayingTrackChanged or
        ITunesEventDispId.PlayerPlayingTrackInfoChanged or
        ITunesEventDispId.ComCallsEnabled => ITunesEventAction.Refresh,
        _ => ITunesEventAction.Ignore,
    };
}

/// <summary>Coalesces dispatcher refreshes while retaining one update requested during an active refresh.</summary>
internal sealed class ITunesRefreshScheduler
{
    private readonly Lock _lock = new();
    private bool _isQueued;
    private bool _isRefreshing;
    private bool _needsFollowUp;

    public bool HasPendingRefresh
    {
        get
        {
            lock (this._lock)
            {
                return this._isQueued || this._isRefreshing || this._needsFollowUp;
            }
        }
    }

    /// <summary>Requests a refresh and returns true only when a dispatcher callback must be enqueued.</summary>
    public bool RequestRefresh()
    {
        lock (this._lock)
        {
            if (this._isRefreshing)
            {
                this._needsFollowUp = true;
                return false;
            }

            if (this._isQueued)
            {
                return false;
            }

            this._isQueued = true;
            return true;
        }
    }

    /// <summary>Begins the callback that was previously queued.</summary>
    public bool TryBeginRefresh()
    {
        lock (this._lock)
        {
            if (!this._isQueued)
            {
                return false;
            }

            this._isQueued = false;
            this._isRefreshing = true;
            return true;
        }
    }

    /// <summary>Completes a refresh and returns true when its follow-up must be queued.</summary>
    public bool CompleteRefresh()
    {
        lock (this._lock)
        {
            if (!this._isRefreshing)
            {
                return false;
            }

            this._isRefreshing = false;
            if (!this._needsFollowUp)
            {
                return false;
            }

            this._needsFollowUp = false;
            this._isQueued = true;
            return true;
        }
    }

    /// <summary>Invalidates queued or follow-up work during disconnect and disposal.</summary>
    public void Clear()
    {
        lock (this._lock)
        {
            this._isQueued = false;
            this._isRefreshing = false;
            this._needsFollowUp = false;
        }
    }
}

/// <summary>Owns one normal .NET process-exit subscription.</summary>
internal sealed class ITunesProcessExitSubscription : IDisposable
{
    private Process? _process;
    private EventHandler? _handler;

    public bool IsAttached => this._process != null;

    public int ProcessId { get; private set; }

    public void Attach(Process process, Action<int> onExited)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(onExited);

        if (this._process != null)
        {
            throw new InvalidOperationException("The process-exit subscription is already attached.");
        }

        var processId = process.Id;
        EventHandler handler = (_, _) => onExited(processId);

        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += handler;
            this._process = process;
            this._handler = handler;
            this.ProcessId = processId;

            if (process.HasExited)
            {
                handler(process, EventArgs.Empty);
            }
        }
        catch
        {
            process.Exited -= handler;
            process.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        var process = Interlocked.Exchange(ref this._process, null);
        var handler = Interlocked.Exchange(ref this._handler, null);
        this.ProcessId = 0;

        if (process == null)
        {
            return;
        }

        if (handler != null)
        {
            try
            {
                process.Exited -= handler;
            }
            catch
            {
            }
        }

        process.Dispose();
    }
}
