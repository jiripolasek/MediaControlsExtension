// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Microsoft.CommandPalette.Extensions;
using Windows.Foundation;
using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal abstract class AsyncInvokableMediaCommand : AsyncInvokableCommand
{
    private readonly IAsyncOperation<GlobalSystemMediaTransportControlsSessionManager> _manager;

    protected AsyncInvokableMediaCommand(IAsyncOperation<GlobalSystemMediaTransportControlsSessionManager> managerGetter)
    {
        ArgumentNullException.ThrowIfNull(managerGetter);

        this._manager = managerGetter;
    }

    protected override async Task<ICommandResult> InvokeAsync()
    {
        return await this.InvokeAsync(await this._manager);
    }

    protected abstract Task<ICommandResult> InvokeAsync(GlobalSystemMediaTransportControlsSessionManager sessionManager);
}