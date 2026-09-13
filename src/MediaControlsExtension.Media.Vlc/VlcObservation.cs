// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Globalization;
using System.Text.Json;

namespace JPSoftworks.MediaControlsExtension.Media.Vlc;

internal sealed record VlcObservation(
    bool HasMedia,
    MediaPlaybackState Playback,
    MediaPropertiesSnapshot Properties,
    MediaTimelinePropertiesSnapshot Timeline)
{
    public bool? Shuffle { get; init; }
    public bool? Repeat { get; init; }
    public bool? Loop { get; init; }
    public VlcArtworkReference? Artwork { get; init; }

    public MediaCapabilities Capabilities => MediaCapabilities.Play | MediaCapabilities.Pause | MediaCapabilities.Stop |
        MediaCapabilities.SkipNext | MediaCapabilities.SkipPrevious |
        (this.Shuffle.HasValue ? MediaCapabilities.ToggleShuffle : MediaCapabilities.None) |
        (this.Repeat.HasValue && this.Loop.HasValue ? MediaCapabilities.ToggleRepeat : MediaCapabilities.None);

    public string? RepeatCommand => (this.Repeat, this.Loop) switch
    {
        (false, false) or (true, true) => "pl_repeat",
        (true, false) or (false, true) => "pl_loop",
        _ => null,
    };

    public static VlcObservation ParseStatus(JsonElement root)
    {
        if (Number(root, "apiversion") != 3 || !Text(root, "version").StartsWith("3.", StringComparison.Ordinal))
        {
            throw new InvalidDataException(Resources.Strings.Protocol_UnsupportedVersion);
        }

        var state = Text(root, "state") switch
        {
            "playing" => MediaPlaybackState.Playing,
            "paused" => MediaPlaybackState.Paused,
            "stopped" => MediaPlaybackState.Stopped,
            _ => throw new InvalidDataException(Resources.Strings.Protocol_UnknownPlaybackState),
        };
        var meta = Property(Property(Property(root, "information"), "category"), "meta");
        var title = Text(meta, "title");
        var genre = Text(meta, "genre");
        var properties = MediaPropertiesSnapshot.Empty(new("VLC")) with
        {
            Title = string.IsNullOrWhiteSpace(title) ? Text(meta, "filename") : title,
            Artist = Text(meta, "artist"),
            AlbumTitle = Text(meta, "album"),
            AlbumArtist = Text(meta, "album_artist"),
            Subtitle = Text(meta, "description"),
            Genres = string.IsNullOrWhiteSpace(genre) ? [] : [genre],
            TrackNumber = TrackNumber(Text(meta, "track_number")),
            AlbumTrackCount = TrackNumber(Text(meta, "track_total")),
        };
        var duration = Seconds(Number(root, "length"));
        var position = Seconds(Number(root, "time"));
        var timeline = new MediaTimelinePropertiesSnapshot(
            TimeSpan.Zero, duration, TimeSpan.Zero, duration, position, DateTimeOffset.UtcNow);
        var itemId = Property(root, "currentplid");
        var id = itemId.ValueKind == JsonValueKind.Number && itemId.TryGetInt64(out var parsedId) ? parsedId : -1;
        var artworkUrl = Text(meta, "artwork_url");
        return new(id >= 0, state, properties, timeline)
        {
            Shuffle = Boolean(root, "random"),
            Repeat = Boolean(root, "repeat"),
            Loop = Boolean(root, "loop"),
            Artwork = id >= 0 && !string.IsNullOrWhiteSpace(artworkUrl)
                ? new(id, artworkUrl, properties.Title, properties.Artist, properties.AlbumTitle)
                : null,
        };
    }

    public VlcObservation WithPlaylist(JsonElement root)
    {
        JsonElement? first = null;
        JsonElement? current = null;
        FindItems(root, ref first, ref current);
        var item = current ?? first;
        if (item is null)
        {
            return this;
        }

        var duration = Seconds(Number(item.Value, "duration"));
        return this with
        {
            HasMedia = true,
            Properties = this.Properties with { Title = Text(item.Value, "name") },
            Timeline = new(TimeSpan.Zero, duration, TimeSpan.Zero, duration, TimeSpan.Zero, DateTimeOffset.UtcNow),
        };
    }

    private static void FindItems(JsonElement node, ref JsonElement? first, ref JsonElement? current)
    {
        if (Text(node, "type") == "leaf")
        {
            first ??= node;
            if (Text(node, "current") == "current")
            {
                current = node;
            }
        }

        var children = Property(node, "children");
        if (children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                FindItems(child, ref first, ref current);
            }
        }
    }

    private static JsonElement Property(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) ? value : default;

    private static string Text(JsonElement node, string name)
    {
        var value = Property(node, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    }

    private static bool? Boolean(JsonElement node, string name) => Property(node, name).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static double Number(JsonElement node, string name)
    {
        var value = Property(node, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : 0;
    }

    private static TimeSpan Seconds(double value) => TimeSpan.FromSeconds(Math.Clamp(value, 0, 315360000));

    private static int TrackNumber(string value) =>
        int.TryParse(value.Split('/')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0;
}

internal sealed record VlcArtworkReference(long ItemId, string Url, string Title, string Artist, string AlbumTitle);