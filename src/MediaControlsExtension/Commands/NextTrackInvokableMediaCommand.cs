// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Foundation;
using Windows.Media.Control;
using JPSoftworks.MediaControlsExtension.Resources;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class NextTrackInvokableMediaCommand : AsyncInvokableMediaCommand
{
    public NextTrackInvokableMediaCommand(IAsyncOperation<GlobalSystemMediaTransportControlsSessionManager> manager) : base(manager)
    {
        // FallbackSkipTrackCommandItem is using this command to update the name
        this.Name = Strings.Command_NextTrack!;
        this.Icon = Icons.SkipNextTrack;
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