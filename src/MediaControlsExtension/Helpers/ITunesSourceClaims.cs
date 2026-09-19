// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Helpers;

/// <summary>GSMTC identities replaced by the direct iTunes provider.</summary>
internal static class ITunesSourceClaims
{
    /// <summary>Gets all GSMTC sessions suppressed while the direct provider is enabled.</summary>
    public static ImmutableArray<MediaBackendSourceClaim> ReplacesGsmtcSources { get; } =
    [
        new("gsmtc", "Apple.iTunes"),
        new("gsmtc.worker", "Apple.iTunes"),
        new("gsmtc", "iTunes.exe"),
        new("gsmtc.worker", "iTunes.exe"),
        new("gsmtc", "49586DaveAntoine.MediaControllerforiTunes_9bzempp7dntjg"),
        new("gsmtc.worker", "49586DaveAntoine.MediaControllerforiTunes_9bzempp7dntjg"),
        new("gsmtc", "49586DaveAntoine.MediaControllerforiTunes_9bzempp7dntjg!App"),
        new("gsmtc.worker", "49586DaveAntoine.MediaControllerforiTunes_9bzempp7dntjg!App"),
    ];
}
