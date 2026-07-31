// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Runtime.InteropServices.Marshalling;
using JPSoftworks.MediaControlsExtension.Interop;

namespace JPSoftworks.MediaControlsExtension.Services;

internal sealed unsafe partial class SystemVolumeMonitor : IDisposable
{
    private readonly Action<SystemVolumeState> _publishState;
    private readonly ILogger _logger;
    private readonly ManualResetEvent _stopRequested = new(false);
    private readonly AutoResetEvent _rebindRequested = new(false);
    private readonly AutoResetEvent _refreshRequested = new(false);
    private readonly WaitHandle[] _waitHandles;
    private readonly Thread _thread;

    private int _disposeState;

    public SystemVolumeMonitor(
        Action<SystemVolumeState> publishState,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(publishState);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this._publishState = publishState;
        this._logger = loggerFactory.CreateLogger<SystemVolumeMonitor>();
        this._waitHandles = [this._stopRequested, this._rebindRequested, this._refreshRequested];
        this._thread = new(this.Run)
        {
            IsBackground = true,
            Name = "System volume monitor",
        };
        this._thread.Start();
    }

    private void Run()
    {
        nint volumeCallbackPointer = 0;
        nint deviceCallbackPointer = 0;
        CoreAudioNative.DeviceEnumerator? enumerator = null;
        CoreAudioNative.AudioEndpointVolume? endpoint = null;
        var deviceCallbackRegistered = false;

        try
        {
            using var comApartment = ComApartment.Enter();

            var volumeCallback = new AudioEndpointVolumeCallback(this.RequestRefresh);
            var deviceCallback = new DefaultAudioDeviceCallback(this.RequestRebind);
            volumeCallbackPointer = (nint)ComInterfaceMarshaller<IAudioEndpointVolumeCallback>.ConvertToUnmanaged(volumeCallback);
            deviceCallbackPointer = (nint)ComInterfaceMarshaller<IMMNotificationClient>.ConvertToUnmanaged(deviceCallback);

            enumerator = CoreAudioNative.CreateDeviceEnumerator();
            enumerator.RegisterEndpointNotificationCallback(deviceCallbackPointer);
            deviceCallbackRegistered = true;

            this.Rebind(enumerator, volumeCallbackPointer, ref endpoint);

            while (true)
            {
                switch (WaitHandle.WaitAny(this._waitHandles))
                {
                    case 0:
                        return;
                    case 1:
                        this.Rebind(enumerator, volumeCallbackPointer, ref endpoint);
                        break;
                    case 2:
                        this.Refresh(endpoint);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            ExtensionLog.UnexpectedError(this._logger, ex);
        }
        finally
        {
            this.Unbind(volumeCallbackPointer, ref endpoint);

            if (deviceCallbackRegistered && enumerator != null)
            {
                try
                {
                    enumerator.UnregisterEndpointNotificationCallback(deviceCallbackPointer);
                }
                catch (Exception ex)
                {
                    ExtensionLog.Warning(
                        this._logger,
                        $"Could not unregister the default audio device callback: {ex.Message}");
                }
            }

            enumerator?.Dispose();

            if (deviceCallbackPointer != 0)
            {
                ComInterfaceMarshaller<IMMNotificationClient>.Free((void*)deviceCallbackPointer);
            }

            if (volumeCallbackPointer != 0)
            {
                ComInterfaceMarshaller<IAudioEndpointVolumeCallback>.Free((void*)volumeCallbackPointer);
            }
        }
    }

    private void Rebind(
        CoreAudioNative.DeviceEnumerator enumerator,
        nint callback,
        ref CoreAudioNative.AudioEndpointVolume? endpoint)
    {
        this.Unbind(callback, ref endpoint);

        try
        {
            var newEndpoint = enumerator.OpenDefaultPlaybackEndpoint();
            var callbackRegistered = false;
            try
            {
                newEndpoint.RegisterControlChangeNotify(callback);
                callbackRegistered = true;
                this._publishState(newEndpoint.ReadState());
                endpoint = newEndpoint;
            }
            catch
            {
                if (callbackRegistered)
                {
                    try
                    {
                        newEndpoint.UnregisterControlChangeNotify(callback);
                    }
                    catch (Exception ex)
                    {
                        ExtensionLog.Warning(
                            this._logger,
                            $"Could not roll back the volume callback registration: {ex.Message}");
                    }
                }

                newEndpoint.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            ExtensionLog.Warning(
                this._logger,
                $"Could not monitor the default playback endpoint: {ex.Message}");
        }
    }

    private void Refresh(CoreAudioNative.AudioEndpointVolume? endpoint)
    {
        if (endpoint == null)
        {
            return;
        }

        try
        {
            this._publishState(endpoint.ReadState());
        }
        catch (Exception ex)
        {
            ExtensionLog.Warning(
                this._logger,
                $"Could not read the default playback endpoint: {ex.Message}");
            this.RequestRebind();
        }
    }

    private void Unbind(
        nint callback,
        ref CoreAudioNative.AudioEndpointVolume? endpoint)
    {
        var oldEndpoint = endpoint;
        endpoint = null;
        if (oldEndpoint == null)
        {
            return;
        }

        try
        {
            oldEndpoint.UnregisterControlChangeNotify(callback);
        }
        catch (Exception ex)
        {
            ExtensionLog.Warning(
                this._logger,
                $"Could not unregister the volume callback: {ex.Message}");
        }
        finally
        {
            oldEndpoint.Dispose();
        }
    }

    private void RequestRebind()
    {
        if (Volatile.Read(ref this._disposeState) == 0)
        {
            this._rebindRequested.Set();
        }
    }

    private void RequestRefresh()
    {
        if (Volatile.Read(ref this._disposeState) == 0)
        {
            this._refreshRequested.Set();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref this._disposeState, 1) != 0)
        {
            return;
        }

        this._stopRequested.Set();
        if (!ReferenceEquals(Thread.CurrentThread, this._thread))
        {
            this._thread.Join();
        }

        this._refreshRequested.Dispose();
        this._rebindRequested.Dispose();
        this._stopRequested.Dispose();
    }
}
