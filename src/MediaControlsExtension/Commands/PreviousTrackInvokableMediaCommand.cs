// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Media.Control;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class PreviousTrackInvokableMediaCommand : AsyncInvokableMediaCommand
{
    public override IconInfo Icon => Icons.SkipPreviousTrack;

    public override string Name => "Previous Track";

    public PreviousTrackInvokableMediaCommand(GlobalSystemMediaTransportControlsSessionManager manager) : base(manager)
    {
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