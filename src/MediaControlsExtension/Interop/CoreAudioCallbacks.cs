// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace JPSoftworks.MediaControlsExtension.Interop;

[GeneratedComInterface]
[Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
internal partial interface IAudioEndpointVolumeCallback
{
    [PreserveSig]
    int OnNotify(nint notificationData);
}

[GeneratedComClass]
internal sealed partial class AudioEndpointVolumeCallback : IAudioEndpointVolumeCallback
{
    private readonly Action _onNotify;

    public AudioEndpointVolumeCallback(Action onNotify)
    {
        ArgumentNullException.ThrowIfNull(onNotify);
        this._onNotify = onNotify;
    }

    public int OnNotify(nint notificationData)
    {
        try
        {
            this._onNotify();
            return 0;
        }
        catch
        {
            return unchecked((int)0x80004005);
        }
    }
}

[GeneratedComInterface]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
internal partial interface IMMNotificationClient
{
    [PreserveSig]
    int OnDeviceStateChanged(nint deviceId, uint newState);

    [PreserveSig]
    int OnDeviceAdded(nint deviceId);

    [PreserveSig]
    int OnDeviceRemoved(nint deviceId);

    [PreserveSig]
    int OnDefaultDeviceChanged(EDataFlow flow, ERole role, nint defaultDeviceId);

    [PreserveSig]
    int OnPropertyValueChanged(nint deviceId, PROPERTYKEY key);
}

[GeneratedComClass]
internal sealed partial class DefaultAudioDeviceCallback : IMMNotificationClient
{
    private readonly Action _onDefaultPlaybackDeviceChanged;

    public DefaultAudioDeviceCallback(Action onDefaultPlaybackDeviceChanged)
    {
        ArgumentNullException.ThrowIfNull(onDefaultPlaybackDeviceChanged);
        this._onDefaultPlaybackDeviceChanged = onDefaultPlaybackDeviceChanged;
    }

    public int OnDeviceStateChanged(nint deviceId, uint newState) => 0;

    public int OnDeviceAdded(nint deviceId) => 0;

    public int OnDeviceRemoved(nint deviceId) => 0;

    public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, nint defaultDeviceId)
    {
        if (flow != EDataFlow.Render || role != ERole.Console)
        {
            return 0;
        }

        try
        {
            this._onDefaultPlaybackDeviceChanged();
            return 0;
        }
        catch
        {
            return unchecked((int)0x80004005);
        }
    }

    public int OnPropertyValueChanged(nint deviceId, PROPERTYKEY key) => 0;
}
