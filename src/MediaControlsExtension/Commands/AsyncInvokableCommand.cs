// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal abstract class AsyncInvokableCommand : InvokableCommand
{
    public override ICommandResult Invoke()
    {
        _ = Task.Run(this.InvokeAsync);

        return CommandResult.Dismiss();
    }

    protected abstract Task<ICommandResult> InvokeAsync();
}