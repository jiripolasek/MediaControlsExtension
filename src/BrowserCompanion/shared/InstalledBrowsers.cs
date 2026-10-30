// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using Microsoft.Win32;

namespace JPSoftworks.MediaControlsExtension.BrowserIntegration;

internal sealed record InstalledBrowser(string DisplayName, string ExecutablePath);

internal static class InstalledBrowsers
{
    internal static InstalledBrowser[] Read()
    {
        var browsers = new Dictionary<string, InstalledBrowser>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            foreach (var browser in Read(hive, view)) browsers.TryAdd(browser.ExecutablePath, browser);
        }
        return [.. browsers.Values.OrderBy(browser => browser.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static List<InstalledBrowser> Read(RegistryHive hive, RegistryView view)
    {
        List<InstalledBrowser> result = [];
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var clients = root.OpenSubKey(@"Software\Clients\StartMenuInternet");
            if (clients is null) return result;
            foreach (var name in clients.GetSubKeyNames())
            {
                try
                {
                    using var client = clients.OpenSubKey(name);
                    using var command = client?.OpenSubKey(@"shell\open\command");
                    var browser = FromRegistration(client?.GetValue(null) as string, command?.GetValue(null) as string);
                    if (browser is not null && File.Exists(browser.ExecutablePath)) result.Add(browser);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return result;
    }

    internal static InstalledBrowser? FromRegistration(string? name, string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        var quoted = command.StartsWith('"');
        var end = quoted ? command.IndexOf('"', 1) : command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (end < 0) return null;
        var length = quoted ? end - 1 : end + 4;
        if (!quoted && command.Length > length && !char.IsWhiteSpace(command[length])) return null;
        var path = command.Substring(quoted ? 1 : 0, length);
        if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            path = Path.GetFullPath(path);
            var display = string.IsNullOrWhiteSpace(name) || name.StartsWith('@') ? Path.GetFileNameWithoutExtension(path) : name.Trim();
            return new(display, path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}