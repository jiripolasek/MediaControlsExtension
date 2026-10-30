// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using JPSoftworks.MediaControlsExtension.BrowserIntegration;
using JPSoftworks.MediaControlsExtension.Media;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal sealed record BrowserConnectionPresentation(
    string Name, MediaConnectionStatus Connection, MediaConnectionSource Source, string? Diagnostic);

internal static class BrowserConnectionPresentations
{
    internal static BrowserConnectionPresentation[] Create(ImmutableArray<MediaBackendState> states, IEnumerable<InstalledBrowser> installed)
    {
        var groups = new Dictionary<string, (string Name, List<MediaConnectionState> Profiles)>(StringComparer.OrdinalIgnoreCase);
        foreach (var browser in installed) groups.TryAdd(browser.ExecutablePath, (browser.DisplayName, []));
        var companion = states.FirstOrDefault(state => state.Id == "browser.companion");
        if (companion?.IsEnabled == true)
        {
            foreach (var reported in companion.Connection.Connections)
            {
                var profile = companion.Status == MediaBackendLifecycleStatus.Ready ? reported : reported with
                {
                    Status = MediaConnectionStatus.Unknown, Source = MediaConnectionSource.Unknown,
                    DiagnosticMessage = companion.DiagnosticMessage
                };
                var name = profile.ApplicationName ?? profile.DisplayName;
                var key = !string.IsNullOrEmpty(profile.ApplicationPath) ? profile.ApplicationPath :
                    !string.IsNullOrEmpty(profile.ApplicationId) ? profile.ApplicationId : name;
                if (!groups.TryGetValue(key, out var group)) group = (name, []);
                group.Profiles.Add(profile);
                groups[key] = group;
            }
        }
        return [.. groups.Values.Select(group =>
        {
            var connection = group.Profiles.Any(profile => profile.Status == MediaConnectionStatus.Connected) ? MediaConnectionStatus.Connected :
                group.Profiles.Any(profile => profile.Status == MediaConnectionStatus.Connecting) ? MediaConnectionStatus.Connecting :
                group.Profiles.Any(profile => profile.Status == MediaConnectionStatus.Unknown) ? MediaConnectionStatus.Unknown :
                MediaConnectionStatus.Disconnected;
            var source = group.Profiles.Any(profile => profile.Source == MediaConnectionSource.Provider &&
                    profile.Status == MediaConnectionStatus.Connected) ? MediaConnectionSource.Provider :
                group.Profiles.Any(profile => profile.Source == MediaConnectionSource.Reconnecting &&
                    profile.Status != MediaConnectionStatus.Unknown) ? MediaConnectionSource.Reconnecting :
                group.Profiles.Any(profile => profile.Status == MediaConnectionStatus.Unknown ||
                    profile.Source == MediaConnectionSource.Unknown) ? MediaConnectionSource.Unknown :
                MediaConnectionSource.Fallback;
            var diagnostic = group.Profiles.FirstOrDefault(profile => profile.Status != MediaConnectionStatus.Connected)?.DiagnosticMessage;
            if (source == MediaConnectionSource.Fallback) diagnostic ??= group.Profiles.FirstOrDefault()?.DiagnosticMessage;
            return new BrowserConnectionPresentation(group.Name, connection, source, diagnostic);
        }).OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)];
    }
}