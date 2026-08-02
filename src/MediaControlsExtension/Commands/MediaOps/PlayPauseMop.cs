// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media;
namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed class PlayPauseMop : MediaSessionOp
{
    public override MediaOperation Operation => MediaOperation.TogglePlayback;

    protected override ValueTask<string> GetSuccessMessageAsync(
        IMediaService mediaService,
        MediaCommandOutcome outcome,
        CancellationToken cancellationToken)
    {
        var session = outcome.SessionId is { } sessionId
            ? mediaService.Sessions.FirstOrDefault(candidate => candidate.Id == sessionId)
            : mediaService.CurrentSession;
        if (session is null)
        {
            return ValueTask.FromResult($"⏯️ {Strings.TogglePlayPause}");
        }

        var message = session.PlaybackInfo.EffectiveState == MediaPlaybackState.Playing
            ? $"⏯️ {Strings.Toast_Playing}"
            : $"⏸️ {Strings.Toast_Paused}";
        return ValueTask.FromResult(message);
    }
}
