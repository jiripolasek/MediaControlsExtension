// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal static partial class WindowManager
{
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int GetWindowText(IntPtr hWnd, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2)] char[] buffer, int nMaxCount);

    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowTextLengthW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowTextLength(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "GetClassNameW")]
    private static partial int GetClassName(IntPtr hWnd, [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2)] char[] lpClassName, int nMaxCount);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    /// <summary>
    /// Get all visible windows
    /// </summary>
    internal static List<WindowInfo> GetAllWindows(int? processId = null)
    {
        var windows = new List<WindowInfo>();

        EnumWindows((hWnd, _) =>
        {
            if (processId.HasValue)
            {
                GetWindowThreadProcessId(hWnd, out var currentProcessId);
                if (currentProcessId != processId.Value)
                {
                    return true; // Skip this window
                }
            }

            if (IsWindowVisible(hWnd))
            {
                var windowInfo = GetWindowInfo(hWnd);
                if (!string.IsNullOrEmpty(windowInfo.Title))
                {
                    windows.Add(windowInfo);
                }
            }
            return true;
        }, IntPtr.Zero);

        return windows;
    }


    /// <summary>
    /// Get all visible windows
    /// </summary>
    internal static List<WindowInfo> GetAllWindowsForAppId(string appId)
    {
        var windows = new List<WindowInfo>();

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var currentProcessId);
            var currentAppId = AppUserModelIdInterop.GetAppUserModelIdForProcess(currentProcessId);
            if (appId != currentAppId)
            {
                return true;
            }

            if (IsWindowVisible(hWnd))
            {
                var windowInfo = GetWindowInfo(hWnd);
                if (!string.IsNullOrEmpty(windowInfo.Title))
                {
                    windows.Add(windowInfo);
                }
            }
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    /// <summary>
    /// Get window information
    /// </summary>
    private static WindowInfo GetWindowInfo(IntPtr hWnd)
    {
        var info = new WindowInfo { Handle = hWnd };

        int length = GetWindowTextLength(hWnd);
        if (length > 0)
        {
            char[] buffer = new char[length + 1];
            GetWindowText(hWnd, buffer, buffer.Length);
            info.Title = new string(buffer);
        }

        GetWindowThreadProcessId(hWnd, out var processId);
        info.ProcessId = processId;

        try
        {
            var process = Process.GetProcessById((int)processId);
            info.ProcessName = process.ProcessName;
            info.MainModulePath = process.MainModule?.FileName;
        }
        catch
        {
            info.ProcessName = "";
            info.MainModulePath = "";
        }

        var classNameBuffer = new char[256];
        var actualClassNameLength = GetClassName(hWnd, classNameBuffer, classNameBuffer.Length);
        info.ClassName = new string(classNameBuffer, 0, actualClassNameLength);

        return info;
    }

    /// <summary>
    /// Bring window to front
    /// </summary>
    public static bool BringWindowToFront(IntPtr hWnd)
    {
        if (IsIconic(hWnd))
        {
            ShowWindow(hWnd, SW_RESTORE);
        }

        ShowWindow(hWnd, SW_SHOW);
        SetForegroundWindow(hWnd);
        return true;
    }
}