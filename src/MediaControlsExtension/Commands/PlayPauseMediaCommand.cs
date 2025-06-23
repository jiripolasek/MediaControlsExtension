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

internal sealed partial class PlayPauseMediaCommand : AsyncInvokableMediaCommand
{
    public PlayPauseMediaCommand(IAsyncOperation<GlobalSystemMediaTransportControlsSessionManager> sessionManager) : base(sessionManager)
    {
        // FallbackPlayCommandItem is using this command to update the name
        // so we can't override the Name property and we've to allow to set it to empty string
        this.Name = Strings.TogglePlayPause!;
        this.Icon = Icons.PlayPause;
    }

    protected override async Task<ICommandResult> InvokeAsync(
        GlobalSystemMediaTransportControlsSessionManager sessionManager)
    {
        var currentSession = sessionManager.GetCurrentSession();
        if (currentSession != null)
        {
            await currentSession.TryTogglePlayPauseAsync()!;
        }

        return CommandResult.Dismiss();
    }
}