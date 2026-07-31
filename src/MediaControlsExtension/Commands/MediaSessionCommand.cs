// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal partial class MediaSessionCommand : MediaInvokableCommand
{
    private readonly IMediaService _mediaService;
    private readonly MediaSessionId _mediaSessionId;
    private readonly MediaSessionOp _mediaSessionOp;

    protected MediaSessionCommand(
        IMediaService mediaService,
        MediaSession mediaSession,
        MediaSessionOp mediaSessionOp,
        MediaCommandResultFactory resultFactory,
        ILoggerFactory loggerFactory)
        : base(resultFactory, loggerFactory)
    {
        this._mediaService = mediaService ?? throw new ArgumentNullException(nameof(mediaService));
        ArgumentNullException.ThrowIfNull(mediaSession);
        this._mediaSessionId = mediaSession.Id;
        this._mediaSessionOp = mediaSessionOp ?? throw new ArgumentNullException(nameof(mediaSessionOp));
    }

    public MediaSessionOp MediaSessionOp => this._mediaSessionOp;

    protected override async Task<ICommandResult> InvokeMediaAsync(CancellationToken cancellationToken)
    {
        var message = await this._mediaSessionOp.InvokeAsync(
            this._mediaService,
            MediaCommandTarget.ForSession(this._mediaSessionId),
            cancellationToken).ConfigureAwait(false);
        return this.CreateMediaCommandResult(message);
    }
}
