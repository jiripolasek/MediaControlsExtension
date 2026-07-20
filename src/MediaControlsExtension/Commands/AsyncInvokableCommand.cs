// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Diagnostics;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal abstract class AsyncInvokableCommand : InvokableCommand
{
    protected virtual ICommandResult TimeoutResult { get; set; } = CommandResult.Dismiss();

    protected virtual TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    public override ICommandResult Invoke()
    {
        var invocation = this.CreateInvocation();
        Logger.LogDebug("Invoking async command " + this.GetType().FullName);
        var stopwatch = Stopwatch.StartNew();

        using var timeoutCts = new CancellationTokenSource();
        var cmdResult = Task.Run(() => this.SafeInvokeAsync(invocation, timeoutCts.Token));
        if (cmdResult.Wait(this.Timeout))
        {
            Logger.LogDebug("Async command " + this.GetType().FullName + " returned after " + stopwatch.Elapsed);
            return cmdResult.Result;
        }

        timeoutCts.Cancel();
        Logger.LogDebug("Async command " + this.GetType().FullName + " timed out " + stopwatch.Elapsed);
        return this.TimeoutResult;
    }

    private async Task<ICommandResult> SafeInvokeAsync(
        Func<CancellationToken, Task<ICommandResult>> invocation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await invocation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return this.TimeoutResult;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
            return CommandResult.KeepOpen();
        }
    }

    protected virtual Func<CancellationToken, Task<ICommandResult>> CreateInvocation() => this.InvokeAsync;

    protected abstract Task<ICommandResult> InvokeAsync(CancellationToken cancellationToken);
}