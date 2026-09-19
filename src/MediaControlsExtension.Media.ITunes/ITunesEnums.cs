// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Media.ITunes;

internal enum ITPlayerState
{
    Stopped = 0,
    Playing = 1,
    FastForward = 2,
    Rewind = 3,
}

internal enum ITPlaylistRepeatMode
{
    Off = 0,
    One = 1,
    All = 2,
}

internal enum ITArtworkFormat
{
    Unknown = 0,
    JPEG = 1,
    PNG = 2,
    BMP = 3,
}

/// <summary>DISPIDs published by the iTunes <c>_IiTunesEvents</c> connection point.</summary>
internal enum ITunesEventDispId
{
    DatabaseChanged = 1,
    PlayerPlay = 2,
    PlayerStop = 3,
    PlayerPlayingTrackChanged = 4,
    PlayerPlayingTrackInfoChanged = 5,
    ComCallsDisabled = 6,
    ComCallsEnabled = 7,
    Quitting = 8,
    AboutToPromptUserToQuit = 9,
    SoundVolumeChanged = 10,
}
