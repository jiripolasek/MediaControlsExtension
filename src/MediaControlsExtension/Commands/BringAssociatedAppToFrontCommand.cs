// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using MediaService = JPSoftworks.MediaControlsExtension.Services.MediaService;
using MediaSource = JPSoftworks.MediaControlsExtension.Model.MediaSource;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class BringAssociatedAppToFrontCommand : InvokableCommand
{
    private readonly MediaService? _mediaService;
    private readonly MediaSource? _mediaSource;

    private BringAssociatedAppToFrontCommand()
    {
        this.Icon = Icons.SwitchApps;
        this.Name = Strings.Command_SwitchToApplication!;
    }
    
    public BringAssociatedAppToFrontCommand(MediaSource mediaSource) : this()
    {
        ArgumentNullException.ThrowIfNull(mediaSource);

        this._mediaSource = mediaSource;
    }

    public BringAssociatedAppToFrontCommand(MediaService mediaService) : this()
    {
        ArgumentNullException.ThrowIfNull(mediaService);

        this._mediaService = mediaService;
    }

    public override ICommandResult Invoke()
    {
        var mediaSource = this._mediaSource;

        if (this._mediaService != null && mediaSource == null)
        {
            mediaSource = this._mediaService.CurrentSource;
        }

        if (mediaSource?.AppInfo == null)
        {
            return CommandResult.Dismiss();
        }

        try
        {
            AppWindowHelper.TryBringToFront(mediaSource.AppInfo, mediaSource.Name);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }

        return CommandResult.Dismiss();
    }
}
