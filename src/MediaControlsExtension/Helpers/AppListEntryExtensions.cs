// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Xml;
using Windows.ApplicationModel.Core;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal static class AppListEntryExtensions
{
    public static bool IsPwaAsync(this AppListEntry appListEntry)
    {
        if (string.IsNullOrWhiteSpace(appListEntry.AppUserModelId))
        {
            return false;
        }

        if (appListEntry.AppInfo?.Package?.InstalledPath == null)
        {
            return false;
        }

        try
        {
            var appId = appListEntry.AppUserModelId.Split('!').LastOrDefault();
            if (string.IsNullOrEmpty(appId))
            {
                return false;
            }

            var package = appListEntry.AppInfo!.Package;
            var manifestPath = Path.Combine(package.InstalledPath, "AppXManifest.xml");

            if (!File.Exists(manifestPath))
            {
                return false;
            }

            var doc = new XmlDocument();
            doc.Load(manifestPath);

            var nsManager = new XmlNamespaceManager(doc.NameTable);
            nsManager.AddNamespace("pkg", "http://schemas.microsoft.com/appx/manifest/foundation/windows10");
            nsManager.AddNamespace("uap10", "http://schemas.microsoft.com/appx/manifest/uap/windows10/10");

            var appNode = doc.SelectSingleNode($"//pkg:Applications/pkg:Application[@Id='{appId}']", nsManager);
            return appNode?.Attributes?["uap10:HostId"]?.Value == "PWA";
        }
        catch
        {
            return false;
        }
    }
}