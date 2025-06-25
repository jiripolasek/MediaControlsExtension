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
            var shellItem = NativeMethods.SHCreateItemInKnownFolder(
                NativeMethods.FOLDERID_AppsFolder,
                NativeMethods.KF_FLAG_DONT_VERIFY,
                appId,
                typeof(IShellItem2).GUID);
            string displayName = shellItem.GetString(ref PropertyKeys.PKEY_ItemNameDisplay);
            string path = shellItem.GetString(ref PropertyKeys.PKEY_Link_TargetParsingPath);

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