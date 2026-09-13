// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Media;

/// <summary>Options captured when subsequent commands are admitted.</summary>
/// <param name="PauseOtherSessionsOnPlay">Requests best-effort pauses of participating, available, pause-capable sessions before Play.</param>
/// <param name="IncludeRemoteSessionsInPauseOthers">Includes remote sessions as both initiators and targets of automatic pausing.</param>
public sealed record MediaServiceOptions(bool PauseOtherSessionsOnPlay, bool IncludeRemoteSessionsInPauseOthers = false)
{
    /// <summary>Gets defaults that leave other sessions playing.</summary>
    public static MediaServiceOptions Default { get; } = new(false);
}