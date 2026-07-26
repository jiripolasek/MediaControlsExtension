// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media;
namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class CurrentSessionCommand : MediaInvokableCommand
{
    private readonly IMediaService _mediaService;
    private readonly MediaSessionOp _mediaSessionOp;

    public CurrentSessionCommand(
        IMediaService mediaService,
        MediaSessionOp mediaSessionOp,
        MediaCommandResultFactory resultFactory,
        string? id = null)
        : base(resultFactory)
    {
        this._mediaService = mediaService ?? throw new ArgumentNullException(nameof(mediaService));
        this._mediaSessionOp = mediaSessionOp ?? throw new ArgumentNullException(nameof(mediaSessionOp));

        if (!string.IsNullOrWhiteSpace(id))
        {
            this.Id = id;
        }
    }

    public MediaSessionOp MediaSessionOp => this._mediaSessionOp;

    public bool CanExecute()
    {
        var session = this._mediaService.CurrentSession;
        return session is not null && this._mediaSessionOp.CanExecute(session);
    }

    protected override async Task<ICommandResult> InvokeMediaAsync(CancellationToken cancellationToken)
    {
        if (this._mediaService.CurrentSession is null)
        {
            return this.CreateMediaCommandResult($"😢 {Strings.Toast_NoCurrentSession}");
        }

        var message = await this._mediaSessionOp.InvokeAsync(
            this._mediaService,
            MediaCommandTarget.CurrentSession,
            cancellationToken).ConfigureAwait(false);
        return this.CreateMediaCommandResult(message);
    }
}