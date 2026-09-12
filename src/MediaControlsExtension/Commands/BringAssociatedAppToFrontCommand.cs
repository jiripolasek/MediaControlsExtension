// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class BringAssociatedAppToFrontCommand : InvokableCommand
{
    private readonly IMediaService _mediaService;
    private readonly MediaCommandTarget _target;

    public BringAssociatedAppToFrontCommand(IMediaService mediaService, MediaSessionId? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        this._mediaService = mediaService;
        this._target = sessionId is { } id ? MediaCommandTarget.ForSession(id) : MediaCommandTarget.CurrentSession;
        this.Icon = Icons.SwitchApps;
        this.Name = Strings.Command_SwitchToApplication!;
    }

    public override ICommandResult Invoke()
    {
        this._mediaService.TrySubmit(new(this._target, MediaOperation.ActivateSource));
        return CommandResult.Dismiss();
    }
}