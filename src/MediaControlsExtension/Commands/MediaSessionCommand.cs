// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using MediaService = JPSoftworks.MediaControlsExtension.Services.MediaService;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal partial class MediaSessionCommand : AsyncInvokableCommand
{
    private readonly MediaService _mediaService;
    private readonly MediaSource _mediaSource;
    private readonly MediaSessionOp _mediaSessionOp;
    private readonly YetAnotherHelper _yetAnotherHelper;

    public MediaSessionOp MediaSessionOp => this._mediaSessionOp;

    protected MediaSessionCommand(MediaService mediaService, MediaSource mediaSource, MediaSessionOp mediaSessionOp, YetAnotherHelper yetAnotherHelper)
    {
        this._mediaService = mediaService;
        this._mediaSource = mediaSource;
        this._mediaSessionOp = mediaSessionOp;
        this._yetAnotherHelper = yetAnotherHelper;
    }

    protected override async Task<ICommandResult> InvokeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var manager = this._mediaService.SessionManager;
            var result = await GsmtcOperationGate.RunAsync(
                _ => this._mediaSessionOp.InvokeAsync(manager, this._mediaSource.Session),
                cancellationToken);
            if (result.Success)
            {
                this._mediaSource.Update();
            }
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