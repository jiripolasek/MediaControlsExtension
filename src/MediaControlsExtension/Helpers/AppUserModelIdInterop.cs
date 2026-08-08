// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Runtime.InteropServices;
using JPSoftworks.MediaControlsExtension.Interop;
using Microsoft.Win32.SafeHandles;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal static partial class AppUserModelIdInterop
{
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetApplicationUserModelId(
        IntPtr hProcess,
        ref int applicationUserModelIdLength,
        char[] applicationUserModelId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwProcessId);

    [LibraryImport("kernel32.dll")]
    private static partial int WaitForSingleObject(
        IntPtr lpHandle,
        int dwMilliseconds);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint SYNCHRONIZE = 0x00100000;
    private const int ERROR_SUCCESS = 0;

    /// <summary>
    /// Gets the AppUserModelId for a process by process ID, or null if not set or not accessible.
    /// </summary>
    public static string? GetAppUserModelIdForProcess(uint processId)
    {
        using var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, processId);
        if (hProcess.IsInvalid || hProcess.IsClosed)
        {
            return null;
        }

        if (WaitForSingleObject(hProcess.DangerousGetHandle(), 0) != NativeMethods.WAIT_TIMEOUT)
        {
            return null; // Process is a zombie, cannot retrieve AppUserModelId
        }

        int length = 2048;
        var sb = new char[length];
        int result = GetApplicationUserModelId(hProcess.DangerousGetHandle(), ref length, sb);
        if (result != ERROR_SUCCESS)
        {
            // A process may be protected or exit while windows are being enumerated.
            // Failure to read one process identity must not abort the EnumWindows callback.
            return null;
        }

        return new string(sb, 0, length > 0 ? length - 1 : 0);
    }
}