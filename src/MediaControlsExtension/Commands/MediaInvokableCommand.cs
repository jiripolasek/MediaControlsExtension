// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

/// <summary>
/// Base for commands submitted through the media service. Maps unexpected
/// failures to user-facing toasts so subclasses only implement the operation.
/// Cancellation is rethrown to preserve <see cref="AsyncInvokableCommand"/>'s
/// timeout semantics.
/// </summary>
internal abstract class MediaInvokableCommand : AsyncInvokableCommand
{
    private readonly MediaCommandResultFactory _resultFactory;

    protected MediaInvokableCommand(MediaCommandResultFactory resultFactory)
    {
        ArgumentNullException.ThrowIfNull(resultFactory);

        this._resultFactory = resultFactory;
    }

    protected override ICommandResult CreateTimeoutResult() =>
        this.CreateMediaCommandResult($"😢 {Strings.Toast_NothingHappened}");

    protected sealed override async Task<ICommandResult> InvokeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await this.InvokeMediaAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
            return this.CreateMediaCommandResult($"😢 {Strings.Toast_NothingHappened}");
        }
    }

    protected abstract Task<ICommandResult> InvokeMediaAsync(CancellationToken cancellationToken);

    protected ICommandResult CreateMediaCommandResult(string? message)
    {
        return this._resultFactory.Create(message);
    }
}
