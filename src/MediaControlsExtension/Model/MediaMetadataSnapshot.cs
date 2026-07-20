// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using Windows.Media;

namespace JPSoftworks.MediaControlsExtension.Model;

internal readonly record struct MediaMetadataSnapshot(
    string Title,
    string AlbumTitle,
    string AlbumArtist,
    string Artist,
    string Subtitle,
    string Genres,
    int? TrackNumber,
    int? AlbumTrackCount,
    TimeSpan? TrackLength,
    string Player,
    string ApplicationId,
    MediaPlaybackType PlaybackType,
    bool IsPlaying,
    bool CanSkipPrevious,
    bool CanSkipNext,
    bool CanToggleShuffle,
    bool CanToggleRepeat)
{
    public static MediaMetadataSnapshot FromMediaSource(MediaSource mediaSource)
    {
        ArgumentNullException.ThrowIfNull(mediaSource);

        return new(
            mediaSource.Name,
            mediaSource.AlbumTitle,
            mediaSource.AlbumArtist,
            mediaSource.Artist,
            mediaSource.Subtitle,
            mediaSource.Genres,
            mediaSource.TrackNumber > 0 ? mediaSource.TrackNumber : null,
            mediaSource.AlbumTrackCount > 0 ? mediaSource.AlbumTrackCount : null,
            mediaSource.TrackLength,
            mediaSource.ApplicationName ?? string.Empty,
            mediaSource.SourceAppUserModelId,
            mediaSource.PlaybackType,
            mediaSource.DisplayedIsPlaying,
            mediaSource.CanSkipPrevious,
            mediaSource.CanSkipNext,
            mediaSource.CanToggleShuffle,
            mediaSource.CanToggleRepeat);
    }
}
