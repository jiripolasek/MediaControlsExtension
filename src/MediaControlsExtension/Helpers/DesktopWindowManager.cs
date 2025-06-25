// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal static class DesktopWindowManager
{
    public static bool SwitchToDesktopAppWindow(string appPath, string? title = null)
    {
        var apps = WindowManager.GetAllWindows().Where(t => t.MainModulePath == appPath).ToList();

        switch (apps.Count)
        {
            case 0:
                return false;
            case 1:
                return WindowManager.BringWindowToFront(apps[0].Handle);
            default:
                {
                    if (string.IsNullOrEmpty(title))
                    {
                        return WindowManager.BringWindowToFront(apps[0].Handle);
                    }

                    var exactMatch = apps.FirstOrDefault(w => w.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
                    if (exactMatch != null)
                    {
                        return WindowManager.BringWindowToFront(exactMatch.Handle);
                    }

                    var matchingWindow = apps.FirstOrDefault(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
                    if (matchingWindow != null)
                    {
                        return WindowManager.BringWindowToFront(matchingWindow.Handle);
                    }

                    return WindowManager.BringWindowToFront(apps[0].Handle);
                }
        }
    }
}