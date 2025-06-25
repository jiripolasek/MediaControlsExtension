// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal static class PwaWindowManager
{
    private static readonly string[] BrowserProcessNames = [
        "msedge",
        "chrome",
        "chromium",
        "brave",
        "opera", // doesn't really support PWAs
        "vivaldi",
        "iron",
        "DuckDuckGo"
    ];

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

        var browserWindows = windows.Where(static w => BrowserProcessNames.Contains(w.ProcessName, StringComparer.OrdinalIgnoreCase)).ToList();

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