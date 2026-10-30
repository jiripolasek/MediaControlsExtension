// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace JPSoftworks.MediaControlsExtension.BrowserIntegration;

internal static partial class PackagedBrowserIntegration
{
    internal const string ConfigurationFile = "packaged-integration.json";
    internal const string NativeHostName = "com.jiripolasek.media_controls";
    internal const string FirefoxExtensionId = "media-controls-companion@jiripolasek.com";
    internal static string FirefoxManifestPath => Path.Combine(Path.GetDirectoryName(ManifestPath)!, "firefox-native-host.json");

    internal static bool IsPackaged(string directory) => File.Exists(Path.Combine(directory, ConfigurationFile));

    internal static string ManifestPath
    {
        get
        {
            var folder = new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
            Marshal.ThrowExceptionForHR(SHGetKnownFolderPath(in folder, 0x10000, 0, out var path));
            try
            {
                return Path.Combine(Marshal.PtrToStringUni(path)!, "JPSoftworks", "MediaControls", "BrowserCompanion", "native-host.json");
            }
            finally
            {
                Marshal.FreeCoTaskMem(path);
            }
        }
    }

    internal static string[] ParseExtensionIds(string value)
    {
        var ids = value.Split([' ', ',', ';', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (ids.Length > 16 || ids.Any(id => id.Length != 32 || id.Any(character => character is < 'a' or > 'p')))
        {
            throw new ArgumentException("Enter extension IDs containing exactly 32 letters from a to p.", nameof(value));
        }

        return ids.Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static async Task RunAsync(string directory, bool remove, string extensionIds, CancellationToken cancellationToken)
    {
        var ids = remove ? [] : ParseExtensionIds(extensionIds);
        var helper = Path.Combine(directory, "BrowserIntegration.ps1");
        if (!IsPackaged(directory) || !File.Exists(helper))
        {
            throw new InvalidOperationException("Install the Media Controls package before setting up browser integration.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var pipeName = $"JPSoftworks.MediaControls.BrowserSetup.{Guid.NewGuid():N}";
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{helper}\" " +
            $"-Action {(remove ? "Remove" : "Register")} -ResultPipe {pipeName} -CallerProcessId {Environment.ProcessId}";
        if (ids.Length > 0) arguments += $" -ExtensionIds {string.Join(',', ids)}";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = arguments,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        }) ?? throw new InvalidOperationException("Could not start browser integration setup.");
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe);
            var response = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            var result = JsonNode.Parse(response ?? throw new IOException("Browser integration setup returned no result."));
            if (result?["success"]?.GetValue<bool>() != true)
            {
                throw new InvalidOperationException(result?["error"]?.GetValue<string>() ?? "Browser integration setup failed.");
            }
        }
        finally
        {
            if (timeout.IsCancellationRequested && !process.HasExited) process.Kill();
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid folder, uint flags, nint token, out nint path);
}