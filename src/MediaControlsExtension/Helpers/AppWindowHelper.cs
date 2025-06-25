// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.ApplicationModel;

namespace JPSoftworks.MediaControlsExtension.Helpers;

/// <summary>
/// Helper to focus windows by AppUserModelID and retrieve application display names.
/// </summary>
internal static class AppWindowHelper
{
    /// <summary>
    /// Attempts to bring the window with the specified AppUserModelID to the front.
    /// Returns true if successful; false if no matching window was found.
    /// </summary>
    public static bool TryBringToFront(object app, string mediaTitle)
    {
        ArgumentNullException.ThrowIfNull(app);

        try
        {
            switch (app)
            {
                case AppInfo appInfo:
                    {
                        var appEntries = appInfo.Package?.GetAppListEntries();
                        if (appEntries is not { Count: > 0 })
                        {
                            return false;
                        }

                        // 1) app can be a PWA app, then we have to find the PWA window manually by matching titles,
                        //    because LaunchAsync() will start a new instance of the PWA app instead switching to the existing one
                        foreach (var appEntry in appEntries)
                        {
                            if (appEntry.IsPwaAsync() && !string.IsNullOrWhiteSpace(appEntry.DisplayInfo?.DisplayName))
                            {
                                var pwaWindowFound = PwaWindowManager.SwitchToPwaWindow(appEntry.DisplayInfo.DisplayName, mediaTitle);
                                if (pwaWindowFound)
                                {
                                    return true;
                                }
                            }
                        }

                        // 2) start packaged app and hope it will switch to the existing instance
                        _ = appEntries[0]?.LaunchAsync();

                        return true;
                    }
                case DesktopAppInfo desktopAppInfo:
                    DesktopWindowManager.SwitchToDesktopAppWindow(desktopAppInfo.Path, mediaTitle);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }

        return false;
    }
}