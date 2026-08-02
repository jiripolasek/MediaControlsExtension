// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Diagnostics;
using JPSoftworks.MediaControlsExtension.Interop;
using Microsoft.Win32;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal static class DesktopAppHelper
{
    private const string AppPathsRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\App Paths";

    public static DesktopAppInfo? GetExecutable(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);

        try
        {
            var (displayName, path) = NativeMethods.GetAppsFolderProperties(appId);

            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                return new DesktopAppInfo(displayName, path, appId, path + ",0");
            }
        }
        catch (Exception ex) when (IsShellItemNotFound(ex))
        {
        }

        return GetRegisteredExecutablePresentation(appId);
    }

    private static DesktopAppInfo? GetRegisteredExecutablePresentation(string appId)
    {
        if (!appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(appId), appId, StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var appPathKey = baseKey.OpenSubKey($@"{AppPathsRegistryPath}\{appId}");
                var executablePath = (appPathKey?.GetValue(null) as string)?.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                {
                    continue;
                }

                var versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
                var displayName = !string.IsNullOrWhiteSpace(versionInfo.FileDescription)
                    ? versionInfo.FileDescription
                    : versionInfo.ProductName;
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = Path.GetFileNameWithoutExtension(appId);
                }

                // An executable-only GSMTC identity does not identify which registered
                // installation owns the session. Use the registration for presentation,
                // but do not claim its path as an activation target.
                return new DesktopAppInfo(displayName, null, appId, executablePath + ",0");
            }
        }

        return null;
    }

    private static bool IsShellItemNotFound(Exception exception)
    {
        return (HRESULT)(uint)exception.HResult is HRESULT.ERROR_FILE_NOT_FOUND
            or HRESULT.ERROR_PATH_NOT_FOUND
            or HRESULT.ERROR_NOT_FOUND;
    }
}
