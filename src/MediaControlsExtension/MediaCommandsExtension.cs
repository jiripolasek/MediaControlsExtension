// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Runtime.InteropServices;

namespace JPSoftworks.MediaControlsExtension;

[Guid("502f0b1d-b778-450c-9803-6c09cb0e6407")]
public sealed partial class MediaControlsExtension : IExtension, IDisposable
{
    private readonly ManualResetEvent _extensionDisposedEvent;

    private readonly MediaControlsExtensionCommandsProvider _provider = new();

    public MediaControlsExtension(ManualResetEvent extensionDisposedEvent)
    {
        this._extensionDisposedEvent = extensionDisposedEvent;
    }

    public object? GetProvider(ProviderType providerType)
    {
        return providerType switch
        {
            ProviderType.Commands => this._provider,
            _ => null
        };
    }

    public void Dispose()
    {
        this._extensionDisposedEvent.Set();
    }
}