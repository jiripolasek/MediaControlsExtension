// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class BringAssociatedAppToFrontCommand : InvokableCommand
{
    private readonly IMediaService _mediaService;
    private readonly MediaSessionViewModelCache _viewModels;
    private readonly MediaSessionId? _sessionId;

    private BringAssociatedAppToFrontCommand(
        IMediaService mediaService,
        MediaSessionViewModelCache viewModels,
        MediaSessionId? sessionId)
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(viewModels);

        this._mediaService = mediaService;
        this._viewModels = viewModels;
        this._sessionId = sessionId;
        this.Icon = Icons.SwitchApps;
        this.Name = Strings.Command_SwitchToApplication!;
    }

    public BringAssociatedAppToFrontCommand(
        IMediaService mediaService,
        MediaSessionViewModelCache viewModels)
        : this(mediaService, viewModels, null)
    {
    }

    public BringAssociatedAppToFrontCommand(
        IMediaService mediaService,
        MediaSessionViewModelCache viewModels,
        MediaSessionId sessionId)
        : this(mediaService, viewModels, (MediaSessionId?)sessionId)
    {
    }

    public override ICommandResult Invoke()
    {
        var session = this._sessionId is { } sessionId
            ? this._mediaService.Sessions.FirstOrDefault(
                session => session.Id == sessionId)
            : this._mediaService.CurrentSession;
        if (session is null)
        {
            return CommandResult.Dismiss();
        }

        var viewModel = this._viewModels.GetOrCreate(session);
        if (viewModel?.AppInfo == null)
        {
            return CommandResult.Dismiss();
        }

        try
        {
            AppWindowHelper.TryBringToFront(
                viewModel.AppInfo,
                viewModel.MediaProperties.Title);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }

        return CommandResult.Dismiss();
    }
}
