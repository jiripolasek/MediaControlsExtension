// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Interop;

namespace JPSoftworks.MediaControlsExtension.Services;

internal sealed partial class SystemVolumeService : IDisposable
{
    private readonly Lock _operationLock = new();
    private readonly Lock _stateLock = new();
    private readonly SystemVolumeMonitor _monitor;

    private SystemVolumeState? _currentState;
    private int _disposeState;

    public event EventHandler<SystemVolumeState>? StateChanged;

    public SystemVolumeService()
    {
        this._monitor = new(this.PublishState);
    }

    public bool TryGetCurrentState(out SystemVolumeState state)
    {
        lock (this._stateLock)
        {
            if (this._currentState is { } currentState)
            {
                state = currentState;
                return true;
            }
        }

        state = default;
        return false;
    }

    public SystemVolumeState GetState(CancellationToken cancellationToken)
    {
        return this.Execute(static endpoint => endpoint.ReadState(), cancellationToken);
    }

    public SystemVolumeState ToggleMute(CancellationToken cancellationToken)
    {
        return this.Execute(
            endpoint =>
            {
                var isMuted = endpoint.IsMuted;
                endpoint.SetMute(!isMuted);
                return new(endpoint.VolumePercent, !isMuted);
            },
            cancellationToken);
    }

    public SystemVolumeState SetMute(bool muted, CancellationToken cancellationToken)
    {
        return this.Execute(
            endpoint =>
            {
                endpoint.SetMute(muted);
                return new(endpoint.VolumePercent, muted);
            },
            cancellationToken);
    }

    public SystemVolumeState SetVolume(int volumePercent, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(volumePercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(volumePercent, 100);

        return this.Execute(
            endpoint =>
            {
                endpoint.SetVolumePercent(volumePercent);
                return endpoint.ReadState();
            },
            cancellationToken);
    }

    public SystemVolumeState ChangeVolume(VolumeChange change, CancellationToken cancellationToken)
    {
        return this.Execute(
            endpoint =>
            {
                endpoint.ChangeVolume(change);
                if (endpoint.IsMuted)
                {
                    endpoint.SetMute(false);
                }

                return endpoint.ReadState();
            },
            cancellationToken);
    }

    private SystemVolumeState Execute(
        Func<CoreAudioNative.AudioEndpointVolume, SystemVolumeState> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this._disposeState) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        SystemVolumeState state;
        lock (this._operationLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref this._disposeState) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            state = ExecuteOnDefaultPlaybackEndpoint(operation);
        }

        this.PublishState(state);
        return state;
    }

    private static SystemVolumeState ExecuteOnDefaultPlaybackEndpoint(
        Func<CoreAudioNative.AudioEndpointVolume, SystemVolumeState> operation)
    {
        using var comApartment = ComApartment.Enter();
        using var endpoint = CoreAudioNative.OpenDefaultPlaybackEndpoint();
        return operation(endpoint);
    }

    private void PublishState(SystemVolumeState state)
    {
        EventHandler<SystemVolumeState>? handlers;
        lock (this._stateLock)
        {
            if (Volatile.Read(ref this._disposeState) != 0 || this._currentState == state)
            {
                return;
            }

            this._currentState = state;
            handlers = this.StateChanged;
        }

        if (handlers == null)
        {
            return;
        }

        foreach (EventHandler<SystemVolumeState> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, state);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref this._disposeState, 1) != 0)
        {
            return;
        }

        this._monitor.Dispose();
        lock (this._stateLock)
        {
            this.StateChanged = null;
            this._currentState = null;
        }
    }
}

internal readonly record struct SystemVolumeState(int VolumePercent, bool IsMuted);

internal enum VolumeChange
{
    Increase,
    Decrease,
}
