// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media;
namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed class SkipNextTrackMop : MediaSessionOp
{
    public override MediaOperation Operation => MediaOperation.SkipNext;

    public override bool CanExecute(MediaSession session) =>
        session.IsAvailable &&
        session.PlaybackInfo.Capabilities.HasFlag(MediaCapabilities.SkipNext);

    protected override ValueTask<string> GetSuccessMessageAsync(
        IMediaService mediaService,
        MediaCommandOutcome outcome,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult($"⏭️ {Strings.Toast_SkippedNext}");

    protected override string GetFailureMessage(object status) => $"🚫 {Strings.Toast_CouldNotSkipNext}";
}