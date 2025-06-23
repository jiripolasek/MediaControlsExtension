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

internal sealed partial class PreviousTrackInvokableMediaCommand : AsyncInvokableMediaCommand
{
    public PreviousTrackInvokableMediaCommand(IAsyncOperation<GlobalSystemMediaTransportControlsSessionManager> manager) : base(manager)
    {
        // FallbackPreviousTrackCommandItem is using this command to update the name
        this.Name = Strings.Command_PreviousTrack!;
        this.Icon = Icons.SkipPreviousTrack;
    }

    protected override async Task<ICommandResult> InvokeAsync(
        GlobalSystemMediaTransportControlsSessionManager sessionManager)
    {
        var currentSession = sessionManager.GetCurrentSession();
        if (currentSession != null)
        {
            await currentSession.TrySkipPreviousAsync()!;
        }

        return CommandResult.Dismiss();
    }
}