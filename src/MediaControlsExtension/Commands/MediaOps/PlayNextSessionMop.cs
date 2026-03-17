// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Globalization;
using System.Text;
using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal abstract class PlayOtherSessionMop : MediaSessionOp
{
    private static readonly CompositeFormat s_switchedToFormat = CompositeFormat.Parse(Strings.Toast_SwitchedTo!);
    private static readonly CompositeFormat s_couldNotSwitchToFormat = CompositeFormat.Parse(Strings.Toast_CouldNotSwitchTo!);
    private static readonly CompositeFormat s_playingNameFormat = CompositeFormat.Parse(Strings.Toast_PlayingName!);

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
            return new($"🚫 {Strings.Toast_NotCurrentSession}", false);
        }

        // we have only one session, so we can't switch
        if (allSessions.Count == 1 && allSessions[0].SourceAppUserModelId == currentSession.SourceAppUserModelId)
        {
            return new($"🚫 {Strings.Toast_NoOtherSessions}", false);
        }

        var currentIndex = allSessions.FindIndex(t => t.SourceAppUserModelId == currentSession.SourceAppUserModelId);
        if (currentIndex < 0 || currentIndex >= allSessions.Count)
        {
            return new($"🚫 {Strings.Toast_NoNextSession}", false);
        }

        var newIndex = (currentIndex + allSessions.Count + this._relativeIndex) % allSessions.Count;
        var nextSession = allSessions[newIndex];

        var nextMediaSource = this._mediaService.Sources.FirstOrDefault(t => t.Session.SourceAppUserModelId == nextSession.SourceAppUserModelId);
        var s = await this.PlayAsync(manager, nextSession, nextMediaSource);

        var applicationName = string.IsNullOrEmpty(nextMediaSource?.ApplicationName) ? "next session" : nextMediaSource?.ApplicationName;

        return s.Success
            ? new($"🔄️ {string.Format(CultureInfo.CurrentCulture, s_switchedToFormat, applicationName, s.Message)}", true)
            : new($"🚫 {string.Format(CultureInfo.CurrentCulture, s_couldNotSwitchToFormat, applicationName, s.Message)}", false);
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
            message = success ? $"⏯️ {string.Format(CultureInfo.CurrentCulture, s_playingNameFormat, nextMediaSource?.Name)}" : $"🚫 {Strings.Toast_CouldNotPlay}";
        }
        else
        {
            message = Strings.Toast_Playing!;
            success = true; // we don't need to pause the session, just switch to it
        }

        return new(message, success);
    }
}

internal sealed class PlayNextSessionMop(SettingsManager settingsManager, MediaService mediaService) : PlayOtherSessionMop(settingsManager, 1, mediaService);

internal sealed class PlayPreviousSessionMop(SettingsManager settingsManager, MediaService mediaService) : PlayOtherSessionMop(settingsManager, -1, mediaService);