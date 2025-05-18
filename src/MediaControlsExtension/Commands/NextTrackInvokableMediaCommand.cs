// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Media.Control;
using JPSoftworks.MediaControlsExtension.Resources;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class NextTrackInvokableMediaCommand : AsyncInvokableMediaCommand
{
    public override IconInfo Icon => Icons.SkipNextTrack;
    public override string Name => Strings.Command_NextTrack!;

    public NextTrackInvokableMediaCommand(GlobalSystemMediaTransportControlsSessionManager manager) : base(manager)
    {
    }

    protected override async Task<ICommandResult> InvokeAsync(GlobalSystemMediaTransportControlsSessionManager sessionManager)
    {
        var currentSession = sessionManager.GetCurrentSession();
        if (currentSession != null)
        {
            await currentSession.TrySkipNextAsync()!;
        }

        return CommandResult.Dismiss();
    }
}