// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using MediaService = JPSoftworks.MediaControlsExtension.Services.MediaService;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class MediaCurrentSessionCommand : AsyncInvokableCommand
{
    private readonly MediaService _mediaService;
    private readonly MediaSessionOp _mediaSessionOp;
    private readonly YetAnotherHelper _yetAnotherHelper;

    public MediaSessionOp MediaSessionOp => this._mediaSessionOp;

    public MediaCurrentSessionCommand(MediaService mediaService,  MediaSessionOp mediaSessionOp, YetAnotherHelper yetAnotherHelper, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(mediaSessionOp);
        ArgumentNullException.ThrowIfNull(yetAnotherHelper);
        
        this._mediaService = mediaService;
        this._mediaSessionOp = mediaSessionOp;
        this._yetAnotherHelper = yetAnotherHelper;

        if (!string.IsNullOrWhiteSpace(id))
        {
            this.Id = id;
        }
    }

    public bool CanExecute()
    {
        var source = this._mediaService.CurrentSource;
        return source != null && this._mediaSessionOp.CanExecute(source);
    }

    protected override async Task<ICommandResult> InvokeAsync(CancellationToken cancellationToken)
    {
        if (!this._mediaService.TryGetSessionManager(out var manager))
        {
            return this._yetAnotherHelper.GetMediaCommandResult($"😢 {Strings.Toast_NoSessionManager}");
        }

        try
        {
            var result = await GsmtcOperationGate.RunAsync(
                async _ =>
                {
                    var session = manager.GetCurrentSession();
                    return session == null
                        ? new MediaSessionOperationResult($"😢 {Strings.Toast_NoCurrentSession}", false)
                        : await this._mediaSessionOp.InvokeAsync(manager, session);
                },
                cancellationToken);
            return this._yetAnotherHelper.GetMediaCommandResult(result.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (GsmtcCircuitOpenException)
        {
            return this._yetAnotherHelper.GetMediaCommandResult($"🚫 {Strings.Toast_MediaControlsUnavailable}");
        }
        catch (Exception ex)
        {
            // Typically a stale GSMTC session (E_BOUNDS); the next refresh
            // rebinds it. Fail this press with a toast instead of leaking.
            Logger.LogError(ex);
            return this._yetAnotherHelper.GetMediaCommandResult($"😢 {Strings.Toast_NothingHappened}");
        }
    }
}