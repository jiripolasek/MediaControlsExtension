// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class BringAssociatedAppToFrontCommand : AsyncInvokableCommand
{
    private readonly IMediaService _mediaService;
    private readonly MediaCommandTarget _target;

    public BringAssociatedAppToFrontCommand(IMediaService mediaService, ILoggerFactory loggerFactory, MediaSessionId? sessionId = null)
        : base(loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        this._mediaService = mediaService;
        this._target = sessionId is { } id ? MediaCommandTarget.ForSession(id) : MediaCommandTarget.CurrentSession;
        this.Icon = Icons.SwitchApps;
        this.Name = Strings.Command_SwitchToApplication!;
    }

    protected override async Task<ICommandResult> InvokeAsync(CancellationToken cancellationToken)
    {
        var submission = this._mediaService.TrySubmit(new(this._target, MediaOperation.ActivateSource));
        // Keep the host invocation alive until the foreground handoff finishes.
        if (submission.Completion is { } completion)
        {
            await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return CommandResult.Dismiss();
    }
}