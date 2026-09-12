// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal static class AppInfoResolver
{
    public static IAppInfo Resolve(string applicationId)
    {
        if (string.IsNullOrWhiteSpace(applicationId))
        {
            return EmptyAppInfo.Instance;
        }

        var appInfo = ModernAppHelper.Get(applicationId);
        if (appInfo?.DisplayInfo is not null)
        {
            return new ModernAppInfo(appInfo, PackageIconHelper.GetBestIconPath(applicationId));
        }

        return (IAppInfo?)DesktopAppHelper.GetExecutable(applicationId) ?? EmptyAppInfo.Instance;
    }
}