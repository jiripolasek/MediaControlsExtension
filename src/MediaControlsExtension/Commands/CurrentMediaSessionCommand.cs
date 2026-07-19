// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal partial class CurrentMediaSessionCommand : AsyncInvokableCommand
{
    private readonly Task<GlobalSystemMediaTransportControlsSessionManager> _managerGetter;
    private readonly MediaSessionOp _mediaSessionOp;
    private readonly YetAnotherHelper _yetAnotherHelper;

    protected CurrentMediaSessionCommand(Task<GlobalSystemMediaTransportControlsSessionManager> managerGetter, MediaSessionOp mediaSessionOp, YetAnotherHelper yetAnotherHelper)
    {
        this._managerGetter = managerGetter;
        this._mediaSessionOp = mediaSessionOp;
        this._yetAnotherHelper = yetAnotherHelper;
    }

    protected override async Task<ICommandResult> InvokeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var manager = await this._managerGetter.WaitAsync(cancellationToken);

            var result = await GsmtcOperationGate.RunAsync(
                async _ =>
                {
                    var session = manager.GetCurrentSession();
                    return session == null
                        ? new MediaSessionOperationResult($"🚫 {Strings.Toast_NothingPlaying}", false)
                        : await this._mediaSessionOp.InvokeAsync(manager, session);
                },
                cancellationToken);
            return this._yetAnotherHelper.GetMediaCommandResult(result.Message);
        }
        catch (GsmtcCircuitOpenException)
        {
            return this._yetAnotherHelper.GetMediaCommandResult($"🚫 {Strings.Toast_MediaControlsUnavailable}");
        }
    }
}