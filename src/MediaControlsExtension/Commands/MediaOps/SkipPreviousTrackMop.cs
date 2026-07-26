// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media;
namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed class SkipPreviousTrackMop : MediaSessionOp
{
    public override MediaOperation Operation => MediaOperation.SkipPrevious;

    public override bool CanExecute(MediaSession session) =>
        session.PlaybackInfo.Capabilities.HasFlag(MediaCapabilities.SkipPrevious);

    protected override ValueTask<string> GetSuccessMessageAsync(
        IMediaService mediaService,
        MediaCommandOutcome outcome,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult($"⏮️ {Strings.Toast_SkippedPrevious}");

    protected override string GetFailureMessage(object status) => $"🚫 {Strings.Toast_CouldNotSkipPrevious}";
}
