// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal abstract class PlayOtherSessionMop : MediaSessionOp
{
    private readonly int _relativeIndex;
    private readonly MediaService _mediaService;
    private readonly SettingsManager _settingsManager;

    protected PlayOtherSessionMop(SettingsManager settingsManager, int relativeIndex, MediaService mediaService)
    {
        this._relativeIndex = relativeIndex;
        this._mediaService = mediaService;
        this._settingsManager = settingsManager;
    }

    public override async Task<MediaSessionOperationResult> InvokeAsync(GlobalSystemMediaTransportControlsSessionManager manager, GlobalSystemMediaTransportControlsSession session)
    {
        var allSessions = new List<GlobalSystemMediaTransportControlsSession>(manager.GetSessions());
        var currentSession = manager.GetCurrentSession();
        if (currentSession == null)
        {
            return new("🚫 Not the current session", false);
        }

        // we have only one session, so we can't switch
        if (allSessions.Count == 1 && allSessions[0].SourceAppUserModelId == currentSession.SourceAppUserModelId)
        {
            return new("🚫 No other sessions to switch to", false);
        }

        var currentIndex = allSessions.FindIndex(t => t.SourceAppUserModelId == currentSession.SourceAppUserModelId);
        if (currentIndex < 0 || currentIndex >= allSessions.Count)
        {
            return new("🚫 No next session found", false);
        }

        var newIndex = (currentIndex + allSessions.Count + this._relativeIndex) % allSessions.Count;
        var nextSession = allSessions[newIndex];

        var nextMediaSource = this._mediaService.Sources.FirstOrDefault(t => t.Session.SourceAppUserModelId == nextSession.SourceAppUserModelId);
        var s = await this.PlayAsync(manager, nextSession, nextMediaSource);

        var applicationName = string.IsNullOrEmpty(nextMediaSource?.ApplicationName) ? "next session" : nextMediaSource?.ApplicationName;

        return s.Success
            ? new($"🔄️ Switched to {applicationName} and {s.Message}", true)
            : new($"🚫 Could not switch to {applicationName}: {s.Message}", false);
    }


    private async Task<MediaSessionOperationResult> PlayAsync(GlobalSystemMediaTransportControlsSessionManager manager, GlobalSystemMediaTransportControlsSession session, MediaSource? nextMediaSource)
    {
        var sessionIsPlaying = session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        bool success;
        string message;
        if (!sessionIsPlaying)
        {
            if (this._settingsManager.PauseOthersOnPlay)
            {
                foreach (var otherSession in manager.GetSessions() ?? [])
                {
                    await otherSession.TryPauseAsync();
                }
            }

            success = session.GetPlaybackInfo().Controls.IsPlayEnabled && await session.TryPlayAsync();
            message = success ? "⏯️ Playing " + nextMediaSource?.Name : "🚫 Could not play track";
        }
        else
        {
            message = "playing";
            success = true; // we don't need to pause the session, just switch to it
        }

        return new(message, success);
    }
}

internal sealed class PlayNextSessionMop(SettingsManager settingsManager, MediaService mediaService) : PlayOtherSessionMop(settingsManager, 1, mediaService);

internal sealed class PlayPreviousSessionMop(SettingsManager settingsManager, MediaService mediaService) : PlayOtherSessionMop(settingsManager, -1, mediaService);