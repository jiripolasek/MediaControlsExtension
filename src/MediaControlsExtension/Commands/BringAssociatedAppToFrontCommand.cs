// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class BringAssociatedAppToFrontCommand : InvokableCommand
{
    private readonly IMediaService _mediaService;
    private readonly ILogger _logger;
    private readonly MediaSessionViewModelCache _viewModels;
    private readonly MediaSessionId? _sessionId;

    private BringAssociatedAppToFrontCommand(
        IMediaService mediaService,
        MediaSessionViewModelCache viewModels,
        MediaSessionId? sessionId,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(viewModels);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        this._mediaService = mediaService;
        this._logger = loggerFactory.CreateLogger<BringAssociatedAppToFrontCommand>();
        this._viewModels = viewModels;
        this._sessionId = sessionId;
        this.Icon = Icons.SwitchApps;
        this.Name = Strings.Command_SwitchToApplication!;
    }

    public BringAssociatedAppToFrontCommand(
        IMediaService mediaService,
        MediaSessionViewModelCache viewModels,
        ILoggerFactory loggerFactory)
        : this(mediaService, viewModels, null, loggerFactory)
    {
    }

    public BringAssociatedAppToFrontCommand(
        IMediaService mediaService,
        MediaSessionViewModelCache viewModels,
        MediaSessionId sessionId,
        ILoggerFactory loggerFactory)
        : this(mediaService, viewModels, (MediaSessionId?)sessionId, loggerFactory)
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
                viewModel.MediaProperties.Title,
                this._logger);
        }
        catch (Exception ex)
        {
            ExtensionLog.UnexpectedError(this._logger, ex);
        }

        return CommandResult.Dismiss();
    }
}
