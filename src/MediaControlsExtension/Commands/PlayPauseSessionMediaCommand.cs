// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Media.Control;
using JPSoftworks.MediaControlsExtension.Resources;
using JPSoftworks.MediaControlsExtension.Threading;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class PlayPauseSessionMediaCommand : AsyncInvokableCommand
{
    private readonly GlobalSystemMediaTransportControlsSessionManager _manager;
    private readonly GlobalSystemMediaTransportControlsSession _session;

    public PlayPauseSessionMediaCommand(
        GlobalSystemMediaTransportControlsSessionManager manager,
        GlobalSystemMediaTransportControlsSession session)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(session);

        this._manager = manager;
        this._session = session;

        this.Name = Strings.Command_PlayPause!;
        this.Icon = Icons.PlayPause;
    }


    protected override async Task<ICommandResult> InvokeAsync()
    {
        await ComThread.BeginInvoke(() =>
        {
            // is the session playing?
            var playbackInfo = this._session.GetPlaybackInfo();
            var isPlaying = playbackInfo?.PlaybackStatus ==
                            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (isPlaying)
            {
                _ = this._session.TryPauseAsync();
            }
            else
            {
                var sessions = this._manager.GetSessions() ?? [];
                foreach (var session in sessions)
                {
                    if (session != this._session && session.GetPlaybackInfo()?.PlaybackStatus ==
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        _ = session.TryPauseAsync();
                    }
                }

                _ = this._session.TryPlayAsync();
            }
        });
        return CommandResult.Dismiss();
    }
}