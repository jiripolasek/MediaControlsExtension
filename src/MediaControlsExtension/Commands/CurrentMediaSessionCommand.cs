// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Foundation;
using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal partial class CurrentMediaSessionCommand : AsyncInvokableCommand
{
    private readonly IAsyncOperation<GlobalSystemMediaTransportControlsSessionManager> _managerGetter;
    private readonly MediaSessionOp _mediaSessionOp;
    private readonly YetAnotherHelper _yetAnotherHelper;

    protected CurrentMediaSessionCommand(IAsyncOperation<GlobalSystemMediaTransportControlsSessionManager> managerGetter, MediaSessionOp mediaSessionOp, YetAnotherHelper yetAnotherHelper)
    {
        this._managerGetter = managerGetter;
        this._mediaSessionOp = mediaSessionOp;
        this._yetAnotherHelper = yetAnotherHelper;
    }

    protected override async Task<ICommandResult> InvokeAsync()
    {
        var manager = await this._managerGetter;

        var session = manager.GetCurrentSession();
        if (session == null)
        {
            return this._yetAnotherHelper.GetMediaCommandResult("🚫 Nothing is playing");
        }

        var result = await this._mediaSessionOp.InvokeAsync(manager, session);
        return this._yetAnotherHelper.GetMediaCommandResult(result.Message);
    }
}