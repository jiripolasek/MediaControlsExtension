// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal abstract class AsyncInvokableCommand : InvokableCommand
{
    protected AsyncInvokableCommand(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this.Logger = loggerFactory.CreateLogger(
            this.GetType().FullName ?? this.GetType().Name);
    }

    protected ILogger Logger { get; }

    protected virtual TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    public override ICommandResult Invoke()
    {
        var diagnostics = new ExtensionOperationDiagnostics(
            $"async command {this.GetType().FullName ?? this.GetType().Name}",
            this.Logger);
        diagnostics.SetStage("capturing command invocation");
        var invocation = this.CreateInvocation();
        diagnostics.SetStage("creating timeout result");
        var timeoutResult = this.CreateTimeoutResult();
        diagnostics.SetStage("scheduling command body");

        using var timeoutCts = new CancellationTokenSource();
        var cmdResult = Task.Run(() => this.SafeInvokeAsync(invocation, diagnostics, timeoutResult, timeoutCts.Token));
        if (cmdResult.Wait(this.Timeout))
        {
            return cmdResult.Result;
        }

        diagnostics.ReportCallerTimeout(this.Timeout);
        timeoutCts.Cancel();
        return timeoutResult;
    }

    private async Task<ICommandResult> SafeInvokeAsync(
        Func<ExtensionOperationDiagnostics, CancellationToken, Task<ICommandResult>> invocation,
        ExtensionOperationDiagnostics diagnostics,
        ICommandResult timeoutResult,
        CancellationToken cancellationToken)
    {
        try
        {
            diagnostics.SetStage("executing command body");
            return await invocation(diagnostics, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return timeoutResult;
        }
        catch (Exception ex)
        {
            ExtensionLog.UnexpectedError(this.Logger, ex);
            return CommandResult.KeepOpen();
        }
        finally
        {
            diagnostics.Complete();
        }
    }

    protected virtual Func<ExtensionOperationDiagnostics, CancellationToken, Task<ICommandResult>> CreateInvocation() =>
        (_, cancellationToken) => this.InvokeAsync(cancellationToken);

    protected virtual ICommandResult CreateTimeoutResult() => CommandResult.Dismiss();

    protected abstract Task<ICommandResult> InvokeAsync(CancellationToken cancellationToken);
}
