using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace BrowserCompanion;

internal static class BrowserIdentity
{
    internal static (string Name, string ExecutablePath) Describe(Process? browser)
    {
        try
        {
            var path = browser?.MainModule?.FileName;
            if (string.IsNullOrEmpty(path)) return (string.Empty, string.Empty);
            var name = FileVersionInfo.GetVersionInfo(path).ProductName;
            return (string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name, path);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException
            or IOException or UnauthorizedAccessException or System.Security.SecurityException) { return (string.Empty, string.Empty); }
    }

    internal static string Resolve(Process? browser)
    {
        try { return FromExecutable(browser?.MainModule?.FileName); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException
            or IOException or UnauthorizedAccessException or System.Security.SecurityException) { return string.Empty; }
    }

    internal static string FromExecutable(string? path, Func<string, string?>? readFirefoxId = null)
    {
        // Only map known installations; executable names alone cannot distinguish channels.
        if (path is null) return string.Empty;
        if (string.Equals(Path.GetFileName(path), "firefox.exe", StringComparison.OrdinalIgnoreCase))
        {
            var id = (readFirefoxId ?? ReadFirefoxId)(Path.GetDirectoryName(path)!);
            return id is { Length: 16 } && id.All(char.IsAsciiHexDigit) ? id : string.Empty;
        }
        var mappings = new (string Suffix, string ApplicationId)[]
        {
            (@"\Microsoft\Edge\Application\msedge.exe", "MSEdge"),
            (@"\Microsoft\Edge Dev\Application\msedge.exe", "MSEdgeDev"),
            (@"\Google\Chrome\Application\chrome.exe", "Chrome")
        };
        return mappings.FirstOrDefault(mapping => path.EndsWith(mapping.Suffix, StringComparison.OrdinalIgnoreCase)).ApplicationId ?? string.Empty;
    }

    private static string? ReadFirefoxId(string directory)
    {
        // Firefox uses the exact installation's taskbar ID for its Windows media session.
        using var machine = Registry.LocalMachine.OpenSubKey(@"Software\Mozilla\Firefox\TaskBarIDs");
        using var user = Registry.CurrentUser.OpenSubKey(@"Software\Mozilla\Firefox\TaskBarIDs");
        return (machine?.GetValue(directory) ?? user?.GetValue(directory)) as string;
    }
}