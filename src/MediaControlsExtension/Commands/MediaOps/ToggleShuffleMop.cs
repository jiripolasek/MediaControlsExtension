// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media;
namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed class ToggleShuffleMop : MediaSessionOp
{
    public override MediaOperation Operation => MediaOperation.ToggleShuffle;

    public override bool CanExecute(MediaSession session) =>
        session.PlaybackInfo.Capabilities.HasFlag(MediaCapabilities.ToggleShuffle);

    protected override ValueTask<string> GetSuccessMessageAsync(
        IMediaService mediaService,
        MediaCommandOutcome outcome,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult($"🔀 {Strings.Command_ToggleShuffle}");

    protected override string GetFailureMessage(object status) => $"🚫 {Strings.Toast_CouldNotToggleShuffle}";
}
