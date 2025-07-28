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
        var manager = this._mediaService.SessionManager;
        if (manager == null)
        {
            return false;
        }

        var session = manager.GetCurrentSession();
        if (session == null)
        {
            return false;
        }

        return this._mediaSessionOp.CanExecute(manager, session);
    }

    protected override async Task<ICommandResult> InvokeAsync()
    {
        var manager = this._mediaService.SessionManager;
        if (manager == null)
        {
            return this._yetAnotherHelper.GetMediaCommandResult("😢 No media session manager found");
        }

        var session = manager.GetCurrentSession();
        if (session == null)
        {
            return this._yetAnotherHelper.GetMediaCommandResult("😢 No current media session found");
        }

        var result = await this._mediaSessionOp.InvokeAsync(manager, session);
        return this._yetAnotherHelper.GetMediaCommandResult(result.Message);
    }
}