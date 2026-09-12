// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Media;

/// <summary>Options captured when subsequent commands are admitted.</summary>
/// <param name="PauseOtherSessionsOnPlay">Requests best-effort pauses of other available, pause-capable sessions before Play.</param>
public sealed record MediaServiceOptions(bool PauseOtherSessionsOnPlay)
{
    /// <summary>Gets defaults that leave other sessions playing.</summary>
    public static MediaServiceOptions Default { get; } = new(false);
}