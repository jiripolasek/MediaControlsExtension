// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Globalization;
using System.Text;
using Windows.Media;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal static class MediaMetadataFormatting
{
    private static readonly CompositeFormat TrackLengthWithHoursFormat = CompositeFormat.Parse("{0}:{1:00}:{2:00}");
    private static readonly CompositeFormat TrackLengthWithoutHoursFormat = CompositeFormat.Parse("{0}:{1:00}");
    private static readonly CompositeFormat TrackNumberWithCountFormat = CompositeFormat.Parse(Strings.Details_TrackNumberWithCount!);

    public static string FormatTrackLength(TimeSpan? trackLength)
    {
        if (trackLength is not { } length || length <= TimeSpan.Zero)
        {
            return Strings.Details_NotAvailable!;
        }

        return length.TotalHours >= 1
            ? string.Format(
                CultureInfo.CurrentCulture,
                TrackLengthWithHoursFormat,
                (long)length.TotalHours,
                length.Minutes,
                length.Seconds)
            : string.Format(
                CultureInfo.CurrentCulture,
                TrackLengthWithoutHoursFormat,
                length.Minutes,
                length.Seconds);
    }

    public static string FormatPlaybackType(MediaPlaybackType playbackType)
    {
        return playbackType switch
        {
            MediaPlaybackType.Music => Strings.Details_MediaType_Music!,
            MediaPlaybackType.Video => Strings.Details_MediaType_Video!,
            MediaPlaybackType.Image => Strings.Details_MediaType_Image!,
            _ => Strings.Details_MediaType_Unknown!
        };
    }

    public static string FormatTrackNumber(int? trackNumber, int? albumTrackCount)
    {
        if (trackNumber is null)
        {
            return Strings.Details_NotAvailable!;
        }

        return albumTrackCount is { } count
            ? string.Format(CultureInfo.CurrentCulture, TrackNumberWithCountFormat, trackNumber, count)
            : trackNumber.Value.ToString(CultureInfo.CurrentCulture);
    }

    public static string ValueOrNotAvailable(string? value)
        => string.IsNullOrWhiteSpace(value) ? Strings.Details_NotAvailable! : value;
}
