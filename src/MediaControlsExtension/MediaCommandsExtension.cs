// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Runtime.InteropServices;
using JPSoftworks.MediaControlsExtension.Media.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.MediaControlsExtension;

[Guid("502f0b1d-b778-450c-9803-6c09cb0e6407")]
public sealed partial class MediaControlsExtension : IExtension, IDisposable
{
    private readonly ManualResetEvent _extensionDisposedEvent;

    private readonly MediaControlsExtensionCommandsProvider _provider;

    public MediaControlsExtension(ManualResetEvent extensionDisposedEvent)
        : this(extensionDisposedEvent, NullLoggerFactory.Instance)
    {
    }

    public MediaControlsExtension(
        ManualResetEvent extensionDisposedEvent,
        ILoggerFactory loggerFactory)
        : this(extensionDisposedEvent, loggerFactory, new MediaWorkerOwner())
    {
    }

    internal MediaControlsExtension(ManualResetEvent extensionDisposedEvent, ILoggerFactory loggerFactory, MediaWorkerOwner workerOwner)
    {
        this._extensionDisposedEvent = extensionDisposedEvent
            ?? throw new ArgumentNullException(nameof(extensionDisposedEvent));
        ArgumentNullException.ThrowIfNull(loggerFactory);
        try
        {
            this._provider = new(loggerFactory, workerOwner);
        }
        catch
        {
            workerOwner.RequestStop();
            throw;
        }
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
        try
        {
            this._provider.Dispose();
        }
        finally
        {
            this._extensionDisposedEvent.Set();
        }
    }
}