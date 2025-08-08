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

    protected override async Task<ICommandResult> InvokeAsync()
    {
        var manager = this._mediaService.SessionManager;
        var result = await this._mediaSessionOp.InvokeAsync(manager, this._mediaSource.Session);
        if (result.Success)
        {
            this._mediaSource.Update();
        }
        return this._yetAnotherHelper.GetMediaCommandResult(result.Message);
    }
}