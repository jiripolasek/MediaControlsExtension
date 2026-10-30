// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using JPSoftworks.MediaControlsExtension.BrowserIntegration;
using JPSoftworks.MediaControlsExtension.Commands;
using Microsoft.Win32;

namespace JPSoftworks.MediaControlsExtension.Pages;

internal sealed partial class BrowserCompanionPage : ContentPage, IDisposable
{
    private readonly Lock _stateLock = new();
    private readonly CompositeFormat _otherBrowsersFormat = CompositeFormat.Parse(Text("OtherBrowsers"));
    private readonly Lazy<InstalledBrowser[]> _installedBrowsers;
    private IMediaService? _mediaService;
    private bool _disposed;
    private readonly Action<bool> _setEnabled;
    private readonly Func<bool> _isEnabled;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Func<(string ExtensionIds, bool IsRegistered)> _getRegistration;
    private readonly Func<bool, string, CancellationToken, Task>? _run;

    internal BrowserCompanionPage(Action<bool> setEnabled, Func<bool> isEnabled, ILoggerFactory loggerFactory,
        Func<(string ExtensionIds, bool IsRegistered)>? getRegistration = null,
        Func<bool, string, CancellationToken, Task>? run = null, Func<InstalledBrowser[]>? getBrowsers = null)
    {
        this._setEnabled = setEnabled;
        this._isEnabled = isEnabled;
        this._loggerFactory = loggerFactory;
        this._getRegistration = getRegistration ?? (() => ReadRegistration(PackagedBrowserIntegration.ManifestPath, ReadNativeHostPath));
        this._run = run;
        this._installedBrowsers = new(getBrowsers ?? InstalledBrowsers.Read);
        this.Id = "com.jpsoftworks.cmdpal.mediacontrols.sources.browser.companion.configuration";
        this.Name = MediaSourcesPage.Text("Configure");
        this.Title = Text("Title");
        this.Icon = new IconInfo("\uE774");
    }

    public override IContent[] GetContent()
    {
        var registration = this._getRegistration();
        var ids = registration.ExtensionIds;
        var enabled = registration.IsRegistered && this._isEnabled();
        var actions = new JsonArray((JsonNode)new JsonObject
        {
            ["type"] = "Action.OpenUrl", ["id"] = "getExtension", ["title"] = Text("Guide"),
            ["url"] = "https://mediacontrols.jiripolasek.com/companions/"
        });
        if (!enabled) actions.Add((JsonNode)new JsonObject
        {
            ["type"] = "Action.Submit", ["id"] = "enableIntegration", ["title"] = Text("Enable"), ["associatedInputs"] = "none",
            ["data"] = new JsonObject { ["remove"] = false }
        });
        actions.Add((JsonNode)AdvancedAction(ids));

        var template = new JsonObject
        {
            ["type"] = "AdaptiveCard", ["version"] = "1.5",
            ["body"] = new JsonArray(
                new JsonObject { ["type"] = "TextBlock", ["text"] = Text("Help"), ["wrap"] = true },
                new JsonObject
                {
                    ["type"] = "TextBlock", ["id"] = "integrationStatus", ["text"] = Text("EnabledStatus"),
                    ["isVisible"] = enabled, ["color"] = "good", ["wrap"] = true,
                },
                new JsonObject { ["type"] = "TextBlock", ["text"] = Text("EnableHelp"), ["isVisible"] = !enabled,
                    ["wrap"] = true, ["size"] = "small", ["isSubtle"] = true, ["spacing"] = "small" },
                new JsonObject { ["type"] = "ActionSet", ["id"] = "setupActions", ["actions"] = actions },
                this.ConnectionContent(this._mediaService?.Backends ?? [])),
        };
        return [new IntegrationForm(this, template.ToJsonString(), ids)];
    }

    private static JsonObject AdvancedAction(string ids) => new()
    {
        ["type"] = "Action.ShowCard", ["id"] = "advancedSetup", ["title"] = Text("Advanced"),
        ["card"] = new JsonObject
        {
            ["type"] = "AdaptiveCard", ["version"] = "1.5",
            ["body"] = new JsonArray(
                new JsonObject { ["type"] = "TextBlock", ["text"] = Text("Developer"), ["weight"] = "bolder" },
                new JsonObject { ["type"] = "TextBlock", ["text"] = Text("DeveloperHelp"), ["wrap"] = true },
                new JsonObject { ["type"] = "Input.Text", ["id"] = "extensionIds", ["label"] = Text("Ids"), ["value"] = ids },
                new JsonObject
                {
                    ["type"] = "ActionSet",
                    ["actions"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "Action.Submit", ["title"] = Text("SaveDeveloper"),
                        ["data"] = new JsonObject { ["developer"] = true },
                    }),
                },
                new JsonObject
                {
                    ["type"] = "TextBlock", ["text"] = Text("RemovalHelp"), ["wrap"] = true,
                    ["isSubtle"] = true, ["spacing"] = "large",
                },
                new JsonObject
                {
                    ["type"] = "ActionSet",
                    ["actions"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "Action.Submit", ["title"] = Text("Remove"), ["associatedInputs"] = "none",
                        ["data"] = new JsonObject { ["remove"] = true },
                    }),
                }),
        },
    };

    internal void Observe(IMediaService mediaService)
    {
        lock (this._stateLock)
        {
            if (this._disposed) return;
            if (this._mediaService is not null) this._mediaService.BackendsChanged -= this.BackendsChanged;
            this._mediaService = mediaService;
            this._mediaService.BackendsChanged += this.BackendsChanged;
        }
    }

    public void Dispose()
    {
        lock (this._stateLock)
        {
            this._disposed = true;
            if (this._mediaService is not null) this._mediaService.BackendsChanged -= this.BackendsChanged;
            this._mediaService = null;
        }
    }

    private void BackendsChanged(object? sender, EventArgs args)
    {
        lock (this._stateLock)
        {
            if (!this._disposed) this.RaiseItemsChanged();
        }
    }

    private JsonObject ConnectionContent(ImmutableArray<MediaBackendState> states)
    {
        var items = new JsonArray(
            new JsonObject { ["type"] = "TextBlock", ["text"] = Text("Connections"), ["weight"] = "bolder", ["wrap"] = true });
        var windows = states.FirstOrDefault(state => state.IsEnabled && state.Id is "gsmtc" or "gsmtc.worker");
        var windowsSource = windows?.DisplayName ?? Strings.ResourceManager.GetString("Settings_Backend_GsmtcWorker_Title", Strings.Culture)!;
        if (windows is null) windowsSource += $" ({MediaSourcesPage.Text("Disabled")})";
        else if (windows.Status != MediaBackendLifecycleStatus.Ready)
            windowsSource += $" ({MediaSourcesPage.Text("Lifecycle_" + windows.Status)})";
        var rows = BrowserConnectionPresentations.Create(states, this._installedBrowsers.Value);
        var inactive = new JsonArray(
            new JsonObject { ["type"] = "TextBlock", ["text"] = Text("ConnectionsHelp"), ["wrap"] = true, ["size"] = "small", ["isSubtle"] = true },
            BrowserColumns(Text("Browser"), MediaSourcesPage.Text("Connection"), Text("MediaSource"), header: true));
        var activeCount = rows.Count(row => row.Connection != MediaConnectionStatus.Disconnected);
        if (activeCount > 0) items.Add((JsonNode)BrowserColumns(Text("Browser"), MediaSourcesPage.Text("Connection"), Text("MediaSource"), header: true));
        else if (rows.Length > 0)
        {
            items.Add((JsonNode)new JsonObject { ["type"] = "TextBlock", ["text"] = Text("NoConnections"), ["wrap"] = true, ["spacing"] = "small" });
            items.Add((JsonNode)new JsonObject { ["type"] = "TextBlock", ["text"] = $"{Text("MediaSource")}: {windowsSource}",
                ["wrap"] = true, ["size"] = "small", ["isSubtle"] = true, ["spacing"] = "small" });
        }
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var source = row.Source switch
            {
                MediaConnectionSource.Provider => Strings.ResourceManager.GetString("Settings_Backend_Browser_Title", Strings.Culture)!,
                MediaConnectionSource.Reconnecting => Text("Waiting"),
                MediaConnectionSource.Unknown => MediaSourcesPage.Text("Connection_Unknown"),
                _ => windowsSource
            };
            var columns = BrowserColumns(row.Name, MediaSourcesPage.Text("Connection_" + row.Connection), source);
            if (row.Connection == MediaConnectionStatus.Connected) columns["columns"]![1]!["items"]![0]!["color"] = "good";
            var contents = new JsonArray((JsonNode)columns);
            if (!string.IsNullOrWhiteSpace(row.Diagnostic) &&
                row.Connection != MediaConnectionStatus.Disconnected &&
                (row.Source != MediaConnectionSource.Fallback || windows?.Status == MediaBackendLifecycleStatus.Ready)) contents.Add((JsonNode)new JsonObject
            {
                ["type"] = "TextBlock", ["text"] = row.Diagnostic, ["wrap"] = true, ["isSubtle"] = true, ["size"] = "small", ["spacing"] = "small"
            });
            var target = row.Connection == MediaConnectionStatus.Disconnected ? inactive : items;
            target.Add((JsonNode)new JsonObject { ["type"] = "Container", ["id"] = $"browserRow-{index}",
                ["separator"] = true, ["spacing"] = "small", ["items"] = contents });
        }
        if (rows.Length > activeCount) items.Add((JsonNode)new JsonObject
        {
            ["type"] = "ActionSet", ["id"] = "otherBrowsers", ["spacing"] = "small",
            ["actions"] = new JsonArray(new JsonObject
            {
                ["type"] = "Action.ShowCard", ["title"] = string.Format(CultureInfo.CurrentCulture, this._otherBrowsersFormat, rows.Length - activeCount),
                ["card"] = new JsonObject { ["type"] = "AdaptiveCard", ["version"] = "1.5", ["body"] = inactive }
            })
        });
        if (rows.Length == 0) items.Add((JsonNode)new JsonObject { ["type"] = "TextBlock", ["text"] = Text("NoBrowsers"), ["wrap"] = true });
        return new JsonObject { ["type"] = "Container", ["id"] = "browserConnections", ["items"] = items, ["spacing"] = "medium", ["separator"] = true };
    }

    private static JsonObject BrowserColumns(string browser, string connection, string source, bool header = false) => new()
    {
        ["type"] = "ColumnSet", ["spacing"] = "small",
        ["columns"] = new JsonArray(Column(browser, 1.4, header), Column(connection, 1, header), Column(source, 1.6, header))
    };

    private static JsonObject Column(string text, double width, bool header) => new()
    {
        ["type"] = "Column", ["width"] = width, ["spacing"] = "small",
        ["items"] = new JsonArray(new JsonObject
        {
            ["type"] = "TextBlock", ["text"] = text, ["wrap"] = true, ["spacing"] = "none",
            ["weight"] = header ? "bolder" : "default", ["size"] = header ? "small" : "default", ["isSubtle"] = header
        })
    };

    internal static string Text(string key) => Strings.ResourceManager.GetString("BrowserIntegration_" + key, Strings.Culture)!;

    internal static (string ExtensionIds, bool IsRegistered) ReadRegistration(string manifestPath, Func<string, string?> readNativeHostPath)
    {
        var chromium = ReadChromiumRegistration(manifestPath, readNativeHostPath);
        if (File.Exists(manifestPath) && !chromium.IsRegistered) return chromium;
        try
        {
            var firefoxPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, "firefox-native-host.json");
            var firefox = JsonNode.Parse(File.ReadAllText(firefoxPath));
            var registered = firefox is JsonObject &&
                firefox["name"]?.GetValue<string>() == PackagedBrowserIntegration.NativeHostName &&
                firefox["type"]?.GetValue<string>() == "stdio" && File.Exists(firefox["path"]?.GetValue<string>()) &&
                firefox["allowed_extensions"] is JsonArray { Count: 1 } extensions &&
                extensions[0]?.GetValue<string>() == PackagedBrowserIntegration.FirefoxExtensionId &&
                string.Equals(readNativeHostPath($@"Software\Mozilla\NativeMessagingHosts\{PackagedBrowserIntegration.NativeHostName}"),
                    firefoxPath, StringComparison.OrdinalIgnoreCase);
            return (chromium.ExtensionIds, registered);
        }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or UnauthorizedAccessException
            or System.Security.SecurityException or InvalidOperationException)
        {
            return (chromium.ExtensionIds, false);
        }
    }

    private static (string ExtensionIds, bool IsRegistered) ReadChromiumRegistration(string manifestPath, Func<string, string?> readNativeHostPath)
    {
        var ids = string.Empty;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(manifestPath)) is not JsonObject manifest
                || manifest["allowed_origins"] is not JsonArray origins)
            {
                return (ids, false);
            }

            var validIds = origins.OfType<JsonValue>()
                .Select(origin => origin.TryGetValue<string>(out var value)
                    && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "chrome-extension"
                    && value == $"chrome-extension://{uri.Host}/" ? uri.Host : string.Empty)
                .Where(id => id.Length == 32 && id.All(character => character is >= 'a' and <= 'p'))
                .ToArray();
            ids = string.Join(", ", validIds.Distinct(StringComparer.Ordinal));
            var registered = validIds.Length is > 0 and <= 16 && validIds.Length == origins.Count
                && manifest["name"] is JsonValue name && name.TryGetValue<string>(out var hostName)
                && hostName == PackagedBrowserIntegration.NativeHostName
                && manifest["type"] is JsonValue type && type.TryGetValue<string>(out var hostType) && hostType == "stdio"
                && manifest["path"] is JsonValue path && path.TryGetValue<string>(out var executable) && File.Exists(executable)
                && string.Equals(readNativeHostPath($@"Software\Microsoft\Edge\NativeMessagingHosts\{PackagedBrowserIntegration.NativeHostName}"),
                    manifestPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(readNativeHostPath($@"Software\Google\Chrome\NativeMessagingHosts\{PackagedBrowserIntegration.NativeHostName}"),
                    manifestPath, StringComparison.OrdinalIgnoreCase);
            return (ids, registered);
        }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return (ids, false);
        }
    }

    private static string? ReadNativeHostPath(string keyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(null) as string;
    }

    private sealed partial class IntegrationForm : FormContent
    {
        private readonly BrowserCompanionPage _owner;
        private readonly string _registeredIds;

        internal IntegrationForm(BrowserCompanionPage owner, string template, string registeredIds)
        {
            this._owner = owner;
            this._registeredIds = registeredIds;
            this.TemplateJson = template;
        }

        public override ICommandResult SubmitForm(string inputs, string data)
        {
            var action = JsonNode.Parse(data);
            var remove = action?["remove"]?.GetValue<bool>() == true;
            var ids = remove ? string.Empty : action?["developer"]?.GetValue<bool>() == true
                ? JsonNode.Parse(inputs)?["extensionIds"]?.GetValue<string>() ?? string.Empty
                : this._registeredIds;
            var result = new BrowserIntegrationCommand(remove, ids, this._owner._setEnabled, this._owner._loggerFactory, this._owner._run).Invoke();
            this._owner.RaiseItemsChanged();
            return result;
        }
    }
}