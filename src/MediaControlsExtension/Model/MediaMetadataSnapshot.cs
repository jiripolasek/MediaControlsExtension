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
    public static MediaMetadataSnapshot FromViewModel(MediaSessionViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        var properties = viewModel.MediaProperties;
        var playback = viewModel.PlaybackInfo;
        return new(
            properties.Title,
            properties.AlbumTitle,
            properties.AlbumArtist,
            properties.Artist,
            properties.Subtitle,
            string.Join(", ", properties.Genres),
            properties.TrackNumber > 0 ? properties.TrackNumber : null,
            properties.AlbumTrackCount > 0 ? properties.AlbumTrackCount : null,
            viewModel.TimelineProperties.Duration,
            viewModel.ApplicationName,
            properties.Application.ApplicationId,
            viewModel.PlaybackType,
            playback.EffectiveState == MediaPlaybackState.Playing,
            playback.Capabilities.HasFlag(MediaCapabilities.SkipPrevious),
            playback.Capabilities.HasFlag(MediaCapabilities.SkipNext),
            playback.Capabilities.HasFlag(MediaCapabilities.ToggleShuffle),
            playback.Capabilities.HasFlag(MediaCapabilities.ToggleRepeat));
    }
}