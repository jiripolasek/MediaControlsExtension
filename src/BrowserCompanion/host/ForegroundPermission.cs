using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using JPSoftworks.MediaControlsExtension.BrowserIntegration;

namespace BrowserCompanion;

internal static partial class ForegroundPermission
{
    private static readonly object TraceGate = new();

    internal static void Trace(string stage, JsonObject message, bool? received = null)
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "foreground-trace.enabled"))) return;
        try
        {
            var foreground = GetForegroundWindow();
            _ = GetWindowThreadProcessId(foreground, out var foregroundProcess);
            var entry = new JsonObject
            {
                ["time"] = DateTimeOffset.Now,
                ["process"] = Environment.ProcessId,
                ["stage"] = stage,
                ["requestId"] = Wire.Text(message, "requestId"),
                ["operation"] = Wire.Text(message, "operation"),
                ["status"] = Wire.Text(message, "status"),
                ["received"] = received,
                ["granted"] = message["allowForeground"]?.GetValue<bool>(),
                ["foregroundProcess"] = foregroundProcess,
                ["foreground"] = foreground.ToString("X")
            }.ToJsonString();
            lock (TraceGate)
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "foreground-trace.log"), entry + Environment.NewLine);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal static bool AllowRelay(NamedPipeClientStream pipe) =>
        GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId) && processId != 0 && AllowSetForegroundWindow(processId);

    internal static bool AllowBridge(NamedPipeServerStream pipe) =>
        GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId) && processId != 0 && AllowSetForegroundWindow(processId);

    internal static bool AllowBrowser(Process? browser)
    {
        try { return browser is not null && !browser.HasExited && AllowSetForegroundWindow((uint)browser.Id); }
        catch (InvalidOperationException) { return false; }
    }

    internal static Process? FindBrowser()
    {
        // Resolve the native host's launcher from Windows, never from page-supplied IDs.
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) return null;
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        var parents = new Dictionary<uint, uint>();
        if (!Process32FirstW(snapshot, ref entry)) return null;
        do { parents[entry.ProcessId] = entry.ParentProcessId; }
        while (Process32NextW(snapshot, ref entry));

        using var current = Process.GetCurrentProcess();
        var childId = (uint)current.Id;
        var childStarted = current.StartTime;
        for (var depth = 0; depth < 3 && parents.TryGetValue(childId, out var parentId) && parentId > 0; depth++)
        {
            Process? parent = null;
            try
            {
                parent = Process.GetProcessById((int)parentId);
                // Retain the browser handle for this bridge lifetime to prevent PID reuse.
                _ = parent.Handle;
                if (parent.HasExited || parent.SessionId != current.SessionId || parent.StartTime > childStarted) return null;
                var name = parent.ProcessName.ToLowerInvariant();
                if (name is "msedge" or "chrome" or "chromium" or "brave" or "vivaldi" or "opera" or "firefox" ||
                    InstalledBrowsers.Read().Any(browser => string.Equals(browser.ExecutablePath, parent.MainModule?.FileName, StringComparison.OrdinalIgnoreCase)))
                {
                    var browser = parent;
                    parent = null;
                    return browser;
                }
                if (name != "cmd") return null;
                childId = parentId;
                childStarted = parent.StartTime;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
            {
                return null;
            }
            finally { parent?.Dispose(); }
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ProcessEntry
    {
        internal uint Size;
        internal uint Usage;
        internal uint ProcessId;
        internal nuint DefaultHeap;
        internal uint ModuleId;
        internal uint Threads;
        internal uint ParentProcessId;
        internal int BasePriority;
        internal uint Flags;
        internal fixed char Executable[260];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);
}