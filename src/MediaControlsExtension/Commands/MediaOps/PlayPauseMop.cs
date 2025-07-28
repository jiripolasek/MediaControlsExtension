// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed class PlayPauseMop : MediaSessionOp
{
    private readonly SettingsManager _settingsManager;

    public PlayPauseMop(SettingsManager settingsManager)
    {
        ArgumentNullException.ThrowIfNull(settingsManager);

        this._settingsManager = settingsManager;
    }

    public override async Task<MediaSessionOperationResult> InvokeAsync(GlobalSystemMediaTransportControlsSessionManager manager, GlobalSystemMediaTransportControlsSession session)
    {
        var sessionIsPlaying = session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        bool success;
        string message;
        if (sessionIsPlaying)
        {
            success = await session.TryPauseAsync();
            message = success ? "⏸️ Paused" : "🚫 Could not pause playback";
        }
        else
        {
            if (this._settingsManager.PauseOthersOnPlay)
            {
                foreach (var otherSession in manager.GetSessions() ?? [])
                {
                    if (otherSession.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        await otherSession.TryPauseAsync();
                    }
                }
            }

            success = session.GetPlaybackInfo().Controls.IsPlayEnabled && await session.TryPlayAsync();
            message = success ? "⏯️ Playing" : "🚫 Could not play track";
        }

        return new(message, success);
    }
}