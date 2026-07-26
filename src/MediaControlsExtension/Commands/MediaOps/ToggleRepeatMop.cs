// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media;
namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed class ToggleRepeatMop : MediaSessionOp
{
    public override MediaOperation Operation => MediaOperation.ToggleRepeat;

    public override bool CanExecute(MediaSession session) =>
        session.PlaybackInfo.Capabilities.HasFlag(MediaCapabilities.ToggleRepeat);

    protected override ValueTask<string> GetSuccessMessageAsync(
        IMediaService mediaService,
        MediaCommandOutcome outcome,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult($"🔁 {Strings.Command_ToggleRepeat}");

    protected override string GetFailureMessage(object status) => $"🚫 {Strings.Toast_CouldNotChangeRepeat}";
}
