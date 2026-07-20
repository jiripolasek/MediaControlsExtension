// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Commands;

/// <summary>
/// Runs a <see cref="MediaSessionOp"/> against the GSMTC current session,
/// awaiting the session manager directly. Independent of MediaService — for
/// top-level and fallback commands created before the service initializes.
/// </summary>
internal partial class StandaloneCurrentSessionCommand : MediaInvokableCommand
{
    private readonly Task<GlobalSystemMediaTransportControlsSessionManager> _managerGetter;
    private readonly MediaSessionOp _mediaSessionOp;

    protected StandaloneCurrentSessionCommand(Task<GlobalSystemMediaTransportControlsSessionManager> managerGetter, MediaSessionOp mediaSessionOp, YetAnotherHelper yetAnotherHelper)
        : base(yetAnotherHelper)
    {
        this._managerGetter = managerGetter;
        this._mediaSessionOp = mediaSessionOp;
    }

    protected override async Task<ICommandResult> InvokeMediaAsync(CancellationToken cancellationToken)
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
        return this.CreateMediaCommandResult(result.Message);
    }
}
