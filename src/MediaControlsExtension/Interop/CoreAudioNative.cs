// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Runtime.InteropServices;

namespace JPSoftworks.MediaControlsExtension.Interop;

internal static unsafe partial class CoreAudioNative
{
    private const uint ClsContextAll = 0x17;
    private const int GetDefaultAudioEndpointSlot = 4;
    private const int RegisterEndpointNotificationCallbackSlot = 6;
    private const int UnregisterEndpointNotificationCallbackSlot = 7;
    private const int ActivateSlot = 3;
    private const int RegisterControlChangeNotifySlot = 3;
    private const int UnregisterControlChangeNotifySlot = 4;
    private const int SetMasterVolumeLevelScalarSlot = 7;
    private const int GetMasterVolumeLevelScalarSlot = 9;
    private const int SetMuteSlot = 14;
    private const int GetMuteSlot = 15;
    private const int VolumeStepUpSlot = 17;
    private const int VolumeStepDownSlot = 18;
    private const int ReleaseSlot = 2;

    private static readonly Guid MMDeviceEnumeratorClassId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid MMDeviceEnumeratorInterfaceId = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid AudioEndpointVolumeInterfaceId = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly Guid EventContext = new("53DEBE88-95E8-45C9-9F88-05EBA84D355B");

    public static AudioEndpointVolume OpenDefaultPlaybackEndpoint()
    {
        using var enumerator = CreateDeviceEnumerator();
        return enumerator.OpenDefaultPlaybackEndpoint();
    }

    public static DeviceEnumerator CreateDeviceEnumerator()
    {
        nint enumerator = 0;
        try
        {
            Marshal.ThrowExceptionForHR(CoCreateInstance(
                MMDeviceEnumeratorClassId,
                0,
                ClsContextAll,
                MMDeviceEnumeratorInterfaceId,
                out enumerator));

            var result = new DeviceEnumerator(enumerator);
            enumerator = 0;
            return result;
        }
        finally
        {
            Release(enumerator);
        }
    }

    internal sealed partial class DeviceEnumerator : IDisposable
    {
        private nint _instance;

        internal DeviceEnumerator(nint instance)
        {
            this._instance = instance;
        }

        private nint Instance => this._instance != 0
            ? this._instance
            : throw new ObjectDisposedException(nameof(DeviceEnumerator));

        public AudioEndpointVolume OpenDefaultPlaybackEndpoint()
        {
            nint device = 0;
            nint endpointVolume = 0;
            try
            {
                var getDefaultAudioEndpoint = (delegate* unmanaged[Stdcall]<nint, EDataFlow, ERole, nint*, int>)GetMethod(this.Instance, GetDefaultAudioEndpointSlot);
                Marshal.ThrowExceptionForHR(getDefaultAudioEndpoint(this.Instance, EDataFlow.Render, ERole.Console, &device));

                var activate = (delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)GetMethod(device, ActivateSlot);
                var interfaceId = AudioEndpointVolumeInterfaceId;
                Marshal.ThrowExceptionForHR(activate(device, &interfaceId, ClsContextAll, 0, &endpointVolume));

                var result = new AudioEndpointVolume(endpointVolume);
                endpointVolume = 0;
                return result;
            }
            finally
            {
                Release(endpointVolume);
                Release(device);
            }
        }

        public void RegisterEndpointNotificationCallback(nint callback)
        {
            var register = (delegate* unmanaged[Stdcall]<nint, nint, int>)GetMethod(this.Instance, RegisterEndpointNotificationCallbackSlot);
            Marshal.ThrowExceptionForHR(register(this.Instance, callback));
        }

        public void UnregisterEndpointNotificationCallback(nint callback)
        {
            var unregister = (delegate* unmanaged[Stdcall]<nint, nint, int>)GetMethod(this.Instance, UnregisterEndpointNotificationCallbackSlot);
            Marshal.ThrowExceptionForHR(unregister(this.Instance, callback));
        }

        public void Dispose()
        {
            var instance = Interlocked.Exchange(ref this._instance, 0);
            Release(instance);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid classId,
        nint outer,
        uint classContext,
        in Guid interfaceId,
        out nint instance);

    private static nint GetMethod(nint instance, int slot)
    {
        return (*(nint**)instance)[slot];
    }

    private static void Release(nint instance)
    {
        if (instance == 0)
        {
            return;
        }

        var release = (delegate* unmanaged[Stdcall]<nint, uint>)GetMethod(instance, ReleaseSlot);
        _ = release(instance);
    }

    internal sealed partial class AudioEndpointVolume : IDisposable
    {
        private nint _instance;

        internal AudioEndpointVolume(nint instance)
        {
            this._instance = instance;
        }

        public bool IsMuted
        {
            get
            {
                var muted = 0;
                var getMute = (delegate* unmanaged[Stdcall]<nint, int*, int>)GetMethod(this.Instance, GetMuteSlot);
                Marshal.ThrowExceptionForHR(getMute(this.Instance, &muted));
                return muted != 0;
            }
        }

        public int VolumePercent
        {
            get
            {
                var volume = 0f;
                var getVolume = (delegate* unmanaged[Stdcall]<nint, float*, int>)GetMethod(this.Instance, GetMasterVolumeLevelScalarSlot);
                Marshal.ThrowExceptionForHR(getVolume(this.Instance, &volume));
                return (int)Math.Round(volume * 100, MidpointRounding.AwayFromZero);
            }
        }

        public SystemVolumeState ReadState() => new(this.VolumePercent, this.IsMuted);

        private nint Instance => this._instance != 0
            ? this._instance
            : throw new ObjectDisposedException(nameof(AudioEndpointVolume));

        public void SetMute(bool muted)
        {
            var setMute = (delegate* unmanaged[Stdcall]<nint, int, Guid*, int>)GetMethod(this.Instance, SetMuteSlot);
            var eventContext = EventContext;
            Marshal.ThrowExceptionForHR(setMute(this.Instance, muted ? 1 : 0, &eventContext));
        }

        public void SetVolumePercent(int volumePercent)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(volumePercent);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(volumePercent, 100);

            var setVolume = (delegate* unmanaged[Stdcall]<nint, float, Guid*, int>)GetMethod(this.Instance, SetMasterVolumeLevelScalarSlot);
            var eventContext = EventContext;
            Marshal.ThrowExceptionForHR(setVolume(this.Instance, volumePercent / 100f, &eventContext));
        }

        public void ChangeVolume(VolumeChange change)
        {
            var slot = change switch
            {
                VolumeChange.Increase => VolumeStepUpSlot,
                VolumeChange.Decrease => VolumeStepDownSlot,
                _ => throw new ArgumentOutOfRangeException(nameof(change)),
            };

            var changeVolume = (delegate* unmanaged[Stdcall]<nint, Guid*, int>)GetMethod(this.Instance, slot);
            var eventContext = EventContext;
            Marshal.ThrowExceptionForHR(changeVolume(this.Instance, &eventContext));
        }

        public void RegisterControlChangeNotify(nint callback)
        {
            var register = (delegate* unmanaged[Stdcall]<nint, nint, int>)GetMethod(this.Instance, RegisterControlChangeNotifySlot);
            Marshal.ThrowExceptionForHR(register(this.Instance, callback));
        }

        public void UnregisterControlChangeNotify(nint callback)
        {
            var unregister = (delegate* unmanaged[Stdcall]<nint, nint, int>)GetMethod(this.Instance, UnregisterControlChangeNotifySlot);
            Marshal.ThrowExceptionForHR(unregister(this.Instance, callback));
        }

        public void Dispose()
        {
            var instance = Interlocked.Exchange(ref this._instance, 0);
            Release(instance);
        }
    }
}

internal enum EDataFlow
{
    Render,
    Capture,
    All,
}

internal enum ERole
{
    Console,
    Multimedia,
    Communications,
}
