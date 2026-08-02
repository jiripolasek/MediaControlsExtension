// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using Windows.Media;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal static class MediaSessionIcons
{
    public static NiceIconInfo CreateDisplayIcon(
        MediaSessionViewModel viewModel,
        bool includeArtwork)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        if (includeArtwork && viewModel.Artwork?.Stream is not null)
        {
            return new(viewModel.Artwork);
        }

        if (!string.IsNullOrWhiteSpace(viewModel.ApplicationIconPath))
        {
            return new(viewModel.ApplicationIconPath);
        }

        return new(GetPlaceholderIcon(viewModel.PlaybackType));
    }

    public static IconInfo GetFallbackIcon(
        string? applicationIconPath,
        MediaPlaybackType playbackType)
    {
        return !string.IsNullOrWhiteSpace(applicationIconPath)
            ? new(applicationIconPath)
            : GetPlaceholderIcon(playbackType);
    }

    private static IconInfo GetPlaceholderIcon(MediaPlaybackType playbackType)
    {
        return playbackType switch
        {
            MediaPlaybackType.Music => Icons.Music,
            MediaPlaybackType.Video => Icons.Video,
            MediaPlaybackType.Image => Icons.Image,
            _ => Icons.Unknown,
        };
    }
}
