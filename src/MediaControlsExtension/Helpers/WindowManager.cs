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
    private static partial bool EnumWindows(EnumWindowsProc enumProc, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int GetWindowText(nint hWnd, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2)] char[] buffer, int nMaxCount);

    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowTextLengthW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowTextLength(nint hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static unsafe partial int GetClassName(nint hWnd, char* lpClassName, int nMaxCount);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;
    private const int CLASS_NAME_CAPACITY = 256;

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
    private static WindowInfo GetWindowInfo(nint hWnd)
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

        info.ClassName = GetClassNameSafe(hWnd);

        return info;
    }

    /// <summary>
    /// Bring window to front
    /// </summary>
    public static bool BringWindowToFront(nint hWnd)
    {
        // We can't unminimize UWP windows manually: the real window lives in ApplicationFrameHost process
        // and we can't find the connection between app process and frame host when minimized.
        if (IsUwpWindow(hWnd))
        {
            return false;
        }

        if (IsIconic(hWnd))
        {
            ShowWindow(hWnd, SW_RESTORE);
        }

        ShowWindow(hWnd, SW_SHOW);
        return SetForegroundWindow(hWnd);
    }

    private static bool IsUwpWindow(nint hWnd)
    {
        var className = GetClassNameSafe(hWnd);
        return className.Equals("Windows.UI.Core.CoreWindow", StringComparison.Ordinal);
    }

    private static unsafe string GetClassNameSafe(nint hWnd)
    {
        Span<char> buffer = stackalloc char[CLASS_NAME_CAPACITY];

        fixed (char* p = buffer)
        {
            int len = GetClassName(hWnd, p, buffer.Length);
            return len != 0 ? new string(buffer.Slice(0, len)) : string.Empty;
        }
    }
}