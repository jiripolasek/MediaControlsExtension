// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Media.Control;
using Microsoft.CommandPalette.Extensions;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal abstract class AsyncInvokableMediaCommand : AsyncInvokableCommand
{
    private readonly GlobalSystemMediaTransportControlsSessionManager _manager;

    protected AsyncInvokableMediaCommand(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);

        this._manager = manager;
    }

    protected override async Task<ICommandResult> InvokeAsync()
    {
        return await this.InvokeAsync(this._manager);
    }

    protected abstract Task<ICommandResult> InvokeAsync(
        GlobalSystemMediaTransportControlsSessionManager sessionManager);
}