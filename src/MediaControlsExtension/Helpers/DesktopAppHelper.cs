// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Runtime.InteropServices;
using JPSoftworks.MediaControlsExtension.Interop;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal static class DesktopAppHelper
{
    public static DesktopAppInfo? GetExecutable(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);

        try
        {
            var (displayName, path) = NativeMethods.GetAppsFolderProperties(appId);

            return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? new DesktopAppInfo(displayName, path, appId, path + ",0")
                : null;
        }
        catch (COMException ex) when ((uint)ex.ErrorCode == (uint)HRESULT.ERROR_NOT_FOUND)
        {
            return null;
        }
    }
}
