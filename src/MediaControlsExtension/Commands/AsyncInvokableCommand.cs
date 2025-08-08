// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Diagnostics;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal abstract class AsyncInvokableCommand : InvokableCommand
{
    protected virtual bool ReturnImmediately { get; set; }

    protected virtual ICommandResult Result { get; set; } = CommandResult.Dismiss();

    protected virtual ICommandResult TimeoutResult { get; set; } = CommandResult.Dismiss();

    protected virtual TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    public override ICommandResult Invoke()
    {
        Logger.LogDebug("Invoking async command " + this.GetType().FullName);
        var stopwatch = Stopwatch.StartNew();

        if (this.ReturnImmediately)
        {
            // If the command is set to return immediately, we just return a dismiss result
            // and invoke the async operation in the background.
            _ = Task.Run(this.SafeInvokeAsync);
            Logger.LogDebug("Async command " + this.GetType().FullName + " returned immediately");
            return this.Result;
        }
        else
        {
            // If the command is not set to return immediately, we will wait for the async operation to complete
            // and return the result.
            var cmdResult = Task.Run(this.SafeInvokeAsync);
            if (cmdResult.Wait(this.Timeout))
            {
                Logger.LogDebug("Async command " + this.GetType().FullName + " returned after " + stopwatch.Elapsed);
                return cmdResult.Result;
            }
            else
            {
                Logger.LogDebug("Async command " + this.GetType().FullName + " timed out " + stopwatch.Elapsed);
                return this.TimeoutResult;
            }
        }
    }

    private Task<ICommandResult> SafeInvokeAsync()
    {
        try
        {
            return this.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
            return Task.FromResult<ICommandResult>(CommandResult.KeepOpen());
        }
    }

    protected abstract Task<ICommandResult> InvokeAsync();
}