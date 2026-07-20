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

    /// <summary>
    /// One-shot entry point (fallback commands): a toggle is resolved from the
    /// live playback snapshot because there is no optimistic state to derive
    /// the target from.
    /// </summary>
    public async Task<MediaSessionOperationResult> InvokeAsync(
        GlobalSystemMediaTransportControlsSessionManager manager,
        GlobalSystemMediaTransportControlsSession session,
        PlaybackIntent intent)
    {
        GsmtcOperationGate.VerifyAccess();

        var effectiveIntent = intent;
        if (intent == PlaybackIntent.Toggle)
        {
            var playbackInfo = session.GetPlaybackInfo();
            effectiveIntent = PlaybackActionPolicy.ResolveIntent(
                playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                playbackInfo.Controls.IsPlayEnabled,
                playbackInfo.Controls.IsPauseEnabled,
                playbackInfo.Controls.IsStopEnabled);
        }

        return await this.ExecuteAsync(manager, session, effectiveIntent);
    }

    /// <summary>
    /// Executes an absolute intent. The command is always sent — play while
    /// playing and pause while paused are harmless no-ops for the player,
    /// whereas skipping the call based on the (lagging) GSMTC snapshot is what
    /// used to desync spammed toggles from the real player state.
    /// </summary>
    public async Task<MediaSessionOperationResult> ExecuteAsync(
        GlobalSystemMediaTransportControlsSessionManager manager,
        GlobalSystemMediaTransportControlsSession session,
        PlaybackIntent intent)
    {
        GsmtcOperationGate.VerifyAccess();

        return intent switch
        {
            PlaybackIntent.Play => await this.PlayAsync(manager, session),
            PlaybackIntent.Pause => await PauseAsync(session),
            PlaybackIntent.Stop => await StopAsync(session),
            _ => new($"😢 {Strings.Toast_NothingHappened}", false)
        };
    }

    private async Task<MediaSessionOperationResult> PlayAsync(
        GlobalSystemMediaTransportControlsSessionManager manager,
        GlobalSystemMediaTransportControlsSession session)
    {
        if (this._settingsManager.PauseOthersOnPlay)
        {
            // Best effort: a dead session must not prevent playback on the
            // target session.
            try
            {
                foreach (var otherSession in manager.GetSessions() ?? [])
                {
                    try
                    {
                        if (!GsmtcSessionCorrelation.IsSameSource(otherSession, session) &&
                            otherSession.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        {
                            await otherSession.TryPauseAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning($"Could not pause another session: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Could not enumerate sessions to pause: {ex.Message}");
            }
        }

        var success = await session.TryPlayAsync();
        return new(success ? $"⏯️ {Strings.Toast_Playing}" : $"🚫 {Strings.Toast_CouldNotPlay}", success);
    }

    private static async Task<MediaSessionOperationResult> PauseAsync(
        GlobalSystemMediaTransportControlsSession session)
    {
        var success = await session.TryPauseAsync();
        return new(success ? $"⏸️ {Strings.Toast_Paused}" : $"🚫 {Strings.Toast_CouldNotPause}", success);
    }

    private static async Task<MediaSessionOperationResult> StopAsync(
        GlobalSystemMediaTransportControlsSession session)
    {
        var success = await session.TryStopAsync();
        return new(success ? $"⏹️ {Strings.Command_Stop}" : $"🚫 {Strings.Command_Stop}", success);
    }
}
