// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using MediaService = JPSoftworks.MediaControlsExtension.Services.MediaService;

namespace JPSoftworks.MediaControlsExtension.Commands;

/// <summary>
/// Runs a <see cref="MediaSessionOp"/> against the GSMTC current session,
/// resolved through an initialized <see cref="MediaService"/>.
/// </summary>
internal sealed partial class CurrentSessionCommand : MediaInvokableCommand
{
    private readonly MediaService _mediaService;
    private readonly MediaSessionOp _mediaSessionOp;

    public MediaSessionOp MediaSessionOp => this._mediaSessionOp;

    public CurrentSessionCommand(MediaService mediaService, MediaSessionOp mediaSessionOp, YetAnotherHelper yetAnotherHelper, string? id = null)
        : base(yetAnotherHelper)
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(mediaSessionOp);

        this._mediaService = mediaService;
        this._mediaSessionOp = mediaSessionOp;

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

    protected override async Task<ICommandResult> InvokeMediaAsync(CancellationToken cancellationToken)
    {
        if (!this._mediaService.TryGetSessionManager(out var manager))
        {
            return this.CreateMediaCommandResult($"😢 {Strings.Toast_NoSessionManager}");
        }

        var result = await GsmtcOperationGate.RunAsync(
            async _ =>
            {
                var session = manager.GetCurrentSession();
                return session == null
                    ? new MediaSessionOperationResult($"😢 {Strings.Toast_NoCurrentSession}", false)
                    : await this._mediaSessionOp.InvokeAsync(manager, session);
            },
            cancellationToken);
        return this.CreateMediaCommandResult(result.Message);
    }
}
