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

    protected override async Task<MediaSessionOperationResult> InvokeUnderGateAsync(GlobalSystemMediaTransportControlsSessionManager manager, GlobalSystemMediaTransportControlsSession session)
    {
        return await this.InvokeAsync(manager, session, PlaybackIntent.Toggle);
    }

    public async Task<MediaSessionOperationResult> InvokeAsync(
        GlobalSystemMediaTransportControlsSessionManager manager,
        GlobalSystemMediaTransportControlsSession session,
        PlaybackIntent intent)
    {
        GsmtcOperationGate.VerifyAccess();

        var playbackInfo = session.GetPlaybackInfo();
        var sessionIsPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        var effectiveIntent = intent == PlaybackIntent.Toggle
            ? PlaybackActionPolicy.ResolveIntent(
                sessionIsPlaying,
                playbackInfo.Controls.IsPauseEnabled,
                playbackInfo.Controls.IsStopEnabled)
            : intent;

        return effectiveIntent switch
        {
            PlaybackIntent.Play => await this.PlayAsync(manager, session, playbackInfo, sessionIsPlaying),
            PlaybackIntent.Pause => await PauseAsync(session, playbackInfo, sessionIsPlaying),
            PlaybackIntent.Stop => await StopAsync(session, playbackInfo),
            _ => new($"😢 {Strings.Toast_NothingHappened}", false)
        };
    }

    private async Task<MediaSessionOperationResult> PlayAsync(
        GlobalSystemMediaTransportControlsSessionManager manager,
        GlobalSystemMediaTransportControlsSession session,
        GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo,
        bool sessionIsPlaying)
    {
        if (sessionIsPlaying)
        {
            return new($"⏯️ {Strings.Toast_Playing}");
        }

        if (!playbackInfo.Controls.IsPlayEnabled)
        {
            return new($"🚫 {Strings.Toast_CouldNotPlay}", false);
        }

        if (this._settingsManager.PauseOthersOnPlay)
        {
            foreach (var otherSession in manager.GetSessions() ?? [])
            {
                if (!GsmtcSessionCorrelation.IsSameSource(otherSession, session) &&
                    otherSession.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    await otherSession.TryPauseAsync();
                }
            }
        }

        var success = await session.TryPlayAsync();
        return new(success ? $"⏯️ {Strings.Toast_Playing}" : $"🚫 {Strings.Toast_CouldNotPlay}", success);
    }

    private static async Task<MediaSessionOperationResult> PauseAsync(
        GlobalSystemMediaTransportControlsSession session,
        GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo,
        bool sessionIsPlaying)
    {
        if (!sessionIsPlaying)
        {
            return new($"⏸️ {Strings.Toast_Paused}");
        }

        var success = playbackInfo.Controls.IsPauseEnabled && await session.TryPauseAsync();
        return new(success ? $"⏸️ {Strings.Toast_Paused}" : $"🚫 {Strings.Toast_CouldNotPause}", success);
    }

    private static async Task<MediaSessionOperationResult> StopAsync(
        GlobalSystemMediaTransportControlsSession session,
        GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo)
    {
        var success = playbackInfo.Controls.IsStopEnabled && await session.TryStopAsync();
        return new(success ? $"⏹️ {Strings.Command_Stop}" : $"🚫 {Strings.Command_Stop}", success);
    }
}