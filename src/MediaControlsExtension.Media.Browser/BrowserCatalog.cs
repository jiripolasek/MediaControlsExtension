// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using System.Globalization;
using System.Resources;
using System.Text.Json;

namespace JPSoftworks.MediaControlsExtension.Media.Browser;

internal sealed record BrowserTarget(
    string ConnectionId, int TabId, string SessionId, long Generation, string Origin,
    MediaPropertiesSnapshot Properties, MediaTimelinePropertiesSnapshot Timeline,
    MediaPlaybackState State, MediaCapabilities Capabilities, string ArtworkUrl, string FaviconUrl, string Browser);

internal sealed record BrowserCatalog(long Revision, ImmutableArray<BrowserTarget> Targets, ImmutableArray<BrowserHealth> Health)
{
    private static readonly ResourceManager Strings = new("JPSoftworks.MediaControlsExtension.Media.Browser.Resources.Strings", typeof(BrowserCatalog).Assembly);

    internal static BrowserCatalog Parse(JsonElement root)
    {
        if (BrowserWire.Text(root, "type") != "catalog" || root.GetProperty("version").GetInt32() != 2)
            throw new InvalidDataException("Update and restart the Browser companion service.");

        var targets = ImmutableArray.CreateBuilder<BrowserTarget>();
        var connections = ImmutableArray.CreateBuilder<BrowserHealth>();
        var connectionIds = new HashSet<string>(StringComparer.Ordinal);
        var items = root.GetProperty("connections");
        if (items.GetArrayLength() > 64) throw new InvalidDataException("Too many browser connections.");
        foreach (var connection in items.EnumerateArray())
        {
            var id = BrowserWire.Text(connection, "connectionId", required: true);
            var profile = BrowserWire.Text(connection, "profileId", required: true);
            var browser = BrowserWire.Text(connection, "browser", required: true);
            var executable = BrowserWire.Text(connection, "browserExecutable");
            if (executable.Length > 4096) throw new InvalidDataException("Invalid browser executable path.");
            if (!connectionIds.Add(id)) throw new InvalidDataException("Duplicate browser connection.");
            var applicationId = BrowserWire.Text(connection, "applicationId");
            var age = connection.GetProperty("heartbeatAgeMs").GetInt64();
            var hasSnapshot = connection.GetProperty("hasSnapshot").GetBoolean();
            if (age < -1 || age > 86400000 || applicationId.Length > 256)
                throw new InvalidDataException("Invalid browser health.");
            if (applicationId is not ("MSEdge" or "MSEdgeDev" or "Chrome") &&
                !(applicationId.Length == 16 && applicationId.All(char.IsAsciiHexDigit))) applicationId = string.Empty;
            var origin = $"{(applicationId.Length > 0 ? applicationId : executable.Length > 0 ? executable.ToUpperInvariant() : browser)}:{profile}";
            var display = $"{browser} ({profile[..Math.Min(profile.Length, 8)]})";
            connections.Add(new(id, origin, display, applicationId, TimeSpan.FromMilliseconds(age), hasSnapshot)
            {
                BrowserName = browser, ExecutablePath = executable
            });
            var players = connection.GetProperty("sessions");
            var tabs = connection.GetProperty("tabs");
            if (players.GetArrayLength() > 256 || tabs.GetArrayLength() > 256)
                throw new InvalidDataException("Too many browser sessions.");
            var sessions = players.EnumerateArray().ToDictionary(player => BrowserWire.Text(player, "id", required: true), StringComparer.Ordinal);
            var tabIds = new HashSet<int>();
            foreach (var tab in tabs.EnumerateArray())
            {
                var tabId = tab.GetProperty("tabId").GetInt32();
                var generation = tab.GetProperty("generation").GetInt64();
                var sessionId = BrowserWire.Text(tab, "sessionId", required: true);
                if (tabId < 0 || !tabIds.Add(tabId) || generation <= 0 || !sessions.TryGetValue(sessionId, out var session) ||
                    session.GetProperty("tabId").GetInt32() != tabId)
                    throw new InvalidDataException("Invalid browser tab binding.");

                var capabilities = MediaCapabilities.None;
                foreach (var capability in session.GetProperty("capabilities").EnumerateArray())
                {
                    capabilities |= capability.GetString() switch
                    {
                        "play" => MediaCapabilities.Play,
                        "pause" => MediaCapabilities.Pause,
                        "nexttrack" => MediaCapabilities.SkipNext,
                        "previoustrack" => MediaCapabilities.SkipPrevious,
                        "activate" => MediaCapabilities.ActivateSource,
                        _ => MediaCapabilities.None
                    };
                }

                var browserState = BrowserWire.Text(session, "state");
                var statusKey = browserState switch
                {
                    "sleeping" => "State_Sleeping",
                    "unresponsive" => "State_Unresponsive",
                    _ => null
                };
                if (browserState == "sleeping") capabilities &= MediaCapabilities.Play | MediaCapabilities.ActivateSource;
                else if (statusKey is not null) capabilities &= MediaCapabilities.ActivateSource;
                var source = new MediaSourceSnapshot(SourceName(session, browser))
                {
                    Details =
                    [
                        new(Strings.GetString("Source_Browser", CultureInfo.CurrentUICulture)!, browser),
                        new(Strings.GetString("Source_Tab", CultureInfo.CurrentUICulture)!, tabId.ToString(CultureInfo.InvariantCulture)),
                        new(Strings.GetString("Source_Page", CultureInfo.CurrentUICulture)!, BrowserWire.Text(session, "pageTitle")),
                        new(Strings.GetString("Source_Status", CultureInfo.CurrentUICulture)!,
                            statusKey is null ? string.Empty : Strings.GetString(statusKey, CultureInfo.CurrentUICulture)!),
                        new(Strings.GetString("Source_Url", CultureInfo.CurrentUICulture)!, BrowserWire.Text(session, "page"))
                    ]
                };
                var title = BrowserWire.Text(session, "title");
                if (string.IsNullOrWhiteSpace(title)) title = BrowserWire.Text(session, "pageTitle");
                if (string.IsNullOrWhiteSpace(title)) title = browser + " #" + tabId.ToString(CultureInfo.InvariantCulture);
                var properties = MediaPropertiesSnapshot.Empty(source) with
                {
                    Title = title,
                    Artist = BrowserWire.Text(session, "artist"),
                    AlbumTitle = BrowserWire.Text(session, "album")
                };
                var duration = Seconds(session, "duration");
                var position = Seconds(session, "position");
                var state = browserState switch
                {
                    "playing" => MediaPlaybackState.Playing,
                    "paused" => MediaPlaybackState.Paused,
                    "ended" => MediaPlaybackState.Stopped,
                    _ => MediaPlaybackState.Unknown
                };
                targets.Add(new(id, tabId, sessionId, generation, origin, properties,
                    new(TimeSpan.Zero, duration, TimeSpan.Zero, duration, position, null), state, capabilities,
                    BrowserImageLoader.ImageUrl(BrowserWire.Text(session, "artwork")),
                    BrowserImageLoader.ImageUrl(BrowserWire.Text(session, "favicon")), browser));
            }
        }

        var revision = root.GetProperty("revision").GetInt64();
        if (revision < 0) throw new InvalidDataException("Invalid browser revision.");
        return new(revision, targets.ToImmutable(), connections.ToImmutable());
    }

    private static TimeSpan Seconds(JsonElement session, string name)
    {
        return session.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out var value) && double.IsFinite(value) && value >= 0 && value <= TimeSpan.MaxValue.TotalSeconds / 2
            ? TimeSpan.FromSeconds(value) : TimeSpan.Zero;
    }

    private static string SourceName(JsonElement session, string browser)
    {
        var name = BrowserWire.Text(session, "siteName").Trim();
        if (name.Length > 0) return name[..Math.Min(name.Length, 128)];
        var url = BrowserWire.Text(session, "tabUrl");
        if (url.Length == 0) url = BrowserWire.Text(session, "page");
        if (Uri.TryCreate(url, UriKind.Absolute, out var page) && page.Scheme is "http" or "https" && page.UserInfo.Length == 0)
        {
            var host = page.IdnHost;
            if (host.Length > 0) return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        }
        return browser;
    }
}