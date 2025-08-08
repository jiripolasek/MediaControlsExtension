// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.ComponentModel;
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
    private const int APPMODEL_ERROR_NO_APPLICATION = 15703;

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
        int hr = GetApplicationUserModelId(hProcess.DangerousGetHandle(), ref length, sb);
        return hr switch
        {
            0 => new string(sb, 0, length > 0 ? length - 1 : 0),
            APPMODEL_ERROR_NO_APPLICATION => null,
            _ => throw new Win32Exception(hr)
        };
    }
}