// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal static class PwaWindowManager
{
    /// <summary>
    /// Find and switch to a PWA window by app name and optionally page title
    /// </summary>
    /// <param name="appName">The PWA app name</param>
    /// <param name="pageTitle">Optional: specific page title to match</param>
    public static bool SwitchToPwaWindow(string appName, string? pageTitle = null)
    {
        var pwaWindow = FindPwaWindow(appName, pageTitle);
        if (pwaWindow != null)
        {
            return WindowManager.BringWindowToFront(pwaWindow.Handle);
        }
        return false;
    }

    /// <summary>
    /// Extract app name from PWA window title (format: "App name - Page title")
    /// </summary>
    private static string ExtractAppNameFromTitle(string windowTitle)
    {
        if (string.IsNullOrEmpty(windowTitle))
            return string.Empty;

        // Look for the first " - " separator
        int separatorIndex = windowTitle.IndexOf(" - ", StringComparison.CurrentCultureIgnoreCase);
        if (separatorIndex > 0)
        {
            return windowTitle[..separatorIndex].Trim();
        }

        // If no separator found, return the whole title
        return windowTitle.Trim();
    }

    /// <summary>
    /// Find a PWA window by app name and optionally page title
    /// </summary>
    /// <param name="appName">The PWA app name</param>
    /// <param name="pageTitle">Optional: specific page title to match</param>
    private static WindowInfo? FindPwaWindow(string appName, string? pageTitle = null)
    {
        var windows = WindowManager.GetAllWindows();

        var browserProcessNames = new[] { "msedge", "chrome", "msedgewebview2" };
        var browserWindows = windows.Where(w => browserProcessNames.Contains(w.ProcessName, StringComparer.OrdinalIgnoreCase)).ToList();

        var appWindows = browserWindows.Where(w =>
        {
            string extractedAppName = ExtractAppNameFromTitle(w.Title);
            return extractedAppName.Equals(appName, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        // If no windows found for the app, try fuzzy matching
        if (appWindows.Count == 0)
        {
            appWindows = [.. browserWindows.Where(w => w.Title.Contains(appName, StringComparison.OrdinalIgnoreCase))];
        }

        // If no page title specified, return the first matching window
        if (string.IsNullOrEmpty(pageTitle))
        {
            return appWindows.FirstOrDefault();
        }

        // If page title is specified, apply matching strategies
        // Strategy 1: Exact page title match
        var exactPageMatch = appWindows.FirstOrDefault(w =>
        {
            string extractedPageTitle = ExtractPageTitleFromWindowTitle(w.Title);
            return extractedPageTitle.Equals(pageTitle, StringComparison.OrdinalIgnoreCase);
        });

        if (exactPageMatch != null)
            return exactPageMatch;

        // Strategy 2: Partial page title match (contains)
        var partialPageMatch = appWindows.FirstOrDefault(w =>
        {
            string extractedPageTitle = ExtractPageTitleFromWindowTitle(w.Title);
            return extractedPageTitle.Contains(pageTitle, StringComparison.OrdinalIgnoreCase);
        });

        if (partialPageMatch != null)
            return partialPageMatch;

        // Strategy 3: Check if page title appears anywhere in the full window title
        var anywhereMatch = appWindows.FirstOrDefault(w =>
            w.Title.Contains(pageTitle, StringComparison.OrdinalIgnoreCase));

        if (anywhereMatch != null)
            return anywhereMatch;

        // If no match found with page title, return first app window anyway
        return appWindows.FirstOrDefault();
    }

    /// <summary>
    /// Extract page title from PWA window title (format: "App name - Page title")
    /// </summary>
    private static string ExtractPageTitleFromWindowTitle(string windowTitle)
    {
        if (string.IsNullOrEmpty(windowTitle))
            return string.Empty;

        // Look for the first " - " separator
        int separatorIndex = windowTitle.IndexOf(" - ", StringComparison.InvariantCultureIgnoreCase);
        if (separatorIndex > 0 && separatorIndex + 3 < windowTitle.Length)
        {
            return windowTitle[(separatorIndex + 3)..].Trim();
        }

        // If no separator found, return empty
        return string.Empty;
    }
}

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
    internal static List<WindowInfo> GetAllWindows()
    {
        var windows = new List<WindowInfo>();

        EnumWindows((hWnd, _) =>
        {
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



internal sealed class WindowInfo
{
    public IntPtr Handle { get; set; }
    public string Title { get; set; } = "";
    public uint ProcessId { get; set; }
    public string? ProcessName { get; set; }
    public string? ClassName { get; set; }
    public string? MainModulePath { get; set; }
}