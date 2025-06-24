// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Runtime.InteropServices;
using JPSoftworks.MediaControlsExtension.Interop;
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
    public static bool TryBringToFront(string appUserModelId, string mediaTitle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appUserModelId);

        try
        {
            var appEntries = AppInfo.GetFromAppUserModelId(appUserModelId)!.Package?.GetAppListEntries();
            if (appEntries is { Count: > 0 } && appEntries[0] is { } appEntry)
            {
                if (appEntry.IsPwaAsync() && !string.IsNullOrWhiteSpace(appEntry.DisplayInfo?.DisplayName))
                {
                    var pwaWindowFound = PwaWindowManager.SwitchToPwaWindow(appEntry.DisplayInfo.DisplayName, mediaTitle);
                    if (pwaWindowFound)
                    {
                        return true;
                    }
                }

                _ = appEntry.LaunchAsync();
                return true;
            }
            return false;
        }
        catch (COMException ex) when ((uint)ex.ErrorCode == (uint)HRESULT.ERROR_NOT_FOUND)
        {
            // nope
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }

        return false;
    }
}