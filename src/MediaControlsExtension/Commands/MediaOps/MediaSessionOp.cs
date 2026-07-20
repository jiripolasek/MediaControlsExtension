// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal abstract class MediaSessionOp
{
    public virtual bool CanExecute(MediaSource source) => true;

    public Task<MediaSessionOperationResult> InvokeAsync(
        GlobalSystemMediaTransportControlsSessionManager manager,
        GlobalSystemMediaTransportControlsSession session)
    {
        GsmtcOperationGate.VerifyAccess();
        return this.InvokeUnderGateAsync(manager, session);
    }

    protected abstract Task<MediaSessionOperationResult> InvokeUnderGateAsync(
        GlobalSystemMediaTransportControlsSessionManager manager,
        GlobalSystemMediaTransportControlsSession session);

    /// <summary>
    /// Best-effort pause of every playing session other than
    /// <paramref name="targetSession"/>. Dead sessions are logged and skipped
    /// so they cannot abort the caller's playback action.
    /// </summary>
    protected static async Task TryPauseOtherSessionsAsync(
        GlobalSystemMediaTransportControlsSessionManager manager,
        GlobalSystemMediaTransportControlsSession targetSession)
    {
        GsmtcOperationGate.VerifyAccess();

        try
        {
            foreach (var otherSession in manager.GetSessions() ?? [])
            {
                try
                {
                    if (!GsmtcSessionCorrelation.IsSameSource(otherSession, targetSession) &&
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
}