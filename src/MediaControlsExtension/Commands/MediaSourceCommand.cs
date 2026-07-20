// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using MediaService = JPSoftworks.MediaControlsExtension.Services.MediaService;

namespace JPSoftworks.MediaControlsExtension.Commands;

/// <summary>
/// Runs a <see cref="MediaSessionOp"/> against one specific
/// <see cref="MediaSource"/>'s session, regardless of which session is current.
/// </summary>
internal partial class MediaSourceCommand : MediaInvokableCommand
{
    private readonly MediaService _mediaService;
    private readonly MediaSource _mediaSource;
    private readonly MediaSessionOp _mediaSessionOp;

    public MediaSessionOp MediaSessionOp => this._mediaSessionOp;

    protected MediaSourceCommand(MediaService mediaService, MediaSource mediaSource, MediaSessionOp mediaSessionOp, YetAnotherHelper yetAnotherHelper)
        : base(yetAnotherHelper)
    {
        this._mediaService = mediaService;
        this._mediaSource = mediaSource;
        this._mediaSessionOp = mediaSessionOp;
    }

    protected override async Task<ICommandResult> InvokeMediaAsync(CancellationToken cancellationToken)
    {
        var manager = this._mediaService.SessionManager;
        var result = await GsmtcOperationGate.RunAsync(
            _ => this._mediaSessionOp.InvokeAsync(manager, this._mediaSource.Session),
            cancellationToken);
        if (result.Success)
        {
            this._mediaSource.Update();
        }
        return this.CreateMediaCommandResult(result.Message);
    }
}
