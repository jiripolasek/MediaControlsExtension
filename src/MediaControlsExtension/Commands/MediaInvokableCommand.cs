// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

/// <summary>
/// Base for commands that talk to GSMTC. Maps the shared failure modes — an
/// open operation-gate circuit and dead sessions (E_BOUNDS) — to user-facing
/// toasts, so subclasses only implement the media operation itself.
/// Cancellation is rethrown to preserve <see cref="AsyncInvokableCommand"/>'s
/// timeout semantics.
/// </summary>
internal abstract class MediaInvokableCommand : AsyncInvokableCommand
{
    private readonly YetAnotherHelper _yetAnotherHelper;

    protected MediaInvokableCommand(YetAnotherHelper yetAnotherHelper)
    {
        ArgumentNullException.ThrowIfNull(yetAnotherHelper);

        this._yetAnotherHelper = yetAnotherHelper;
    }

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
        catch (GsmtcCircuitOpenException)
        {
            return this.CreateMediaCommandResult($"🚫 {Strings.Toast_MediaControlsUnavailable}");
        }
        catch (Exception ex)
        {
            // Typically a stale GSMTC session (E_BOUNDS); the next refresh
            // rebinds it. Fail this press with a toast instead of leaking.
            Logger.LogError(ex);
            return this.CreateMediaCommandResult($"😢 {Strings.Toast_NothingHappened}");
        }
    }

    protected abstract Task<ICommandResult> InvokeMediaAsync(CancellationToken cancellationToken);

    protected ICommandResult CreateMediaCommandResult(string message)
    {
        return this._yetAnotherHelper.GetMediaCommandResult(message);
    }
}
