// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal static class PackageIconHelper
{
    private static readonly string[] LogoAttributes = ["Square44x44Logo", "Square150x150Logo"];
    private static readonly int[] PreferredTargetSizes = [20, 24, 32, 40, 48, 256, 16];

    /// <summary>
    /// Gets the absolute path to the best available logo image for the given UWP AppUserModelId,
    /// trying the preferred target sizes (in order), then scale assets, then base asset.
    /// Throws ArgumentException if appUserModelId is null or empty.
    /// </summary>
    public static string? GetBestIconPath(string appUserModelId)
    {
        var result = GetBestIconPath(appUserModelId, PreferredTargetSizes);

        if (!string.IsNullOrWhiteSpace(result))
        {
            return result;
        }

        return null;
    }

    /// <summary>
    /// Gets the absolute path to the best available logo image for the given UWP AppUserModelId,
    /// using custom target sizes (in order), then scale assets, then base asset.
    /// Throws ArgumentException if appUserModelId is null or empty.
    /// </summary>
    private static string? GetBestIconPath(string appUserModelId, int[] targetSizes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appUserModelId);
        ArgumentNullException.ThrowIfNull(targetSizes);
        ArgumentOutOfRangeException.ThrowIfZero(targetSizes.Length);

        try
        {
            var appInfo = AppInfo.GetFromAppUserModelId(appUserModelId);
            var package = appInfo?.Package;

            if (package is null)
            {
                PackageManager packageManager = new PackageManager();
                var packages = packageManager.FindPackagesForUser(string.Empty, appUserModelId);
                package = packages?.FirstOrDefault();
            }

            if (package is null)
            {
                return null;
            }

            var logoBase = GetLogoBasePath(package);
            if (logoBase is not null && !string.IsNullOrWhiteSpace(package.InstalledLocation?.Path))
            {
                return FindBestLogoAsset(package.InstalledLocation.Path, logoBase, targetSizes);
            }
            else
            {
                return null;
            }
        }
        catch
        {
            return null;
        }
    }

    private static string? GetLogoBasePath(Package package)
    {
        if (string.IsNullOrWhiteSpace(package.InstalledLocation?.Path))
            return null;

        var manifestPath = Path.Combine(package.InstalledLocation.Path, "AppxManifest.xml");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var manifestXml = XDocument.Load(manifestPath);
        var uap = (XNamespace)"http://schemas.microsoft.com/appx/manifest/uap/windows10";
        var visualElements = manifestXml.Descendants(uap + "VisualElements").FirstOrDefault();
        if (visualElements is null)
        {
            return null;
        }

        foreach (var attr in LogoAttributes)
        {
            if (visualElements.Attribute(attr)?.Value is { } val && !string.IsNullOrEmpty(val))
            {
                return val;
            }
        }

        return null;
    }

    private static string? FindBestLogoAsset(string packageRoot, string logoBase, int[] targetSizes)
    {
        var logoFullPath = Path.Combine(packageRoot, logoBase);
        var assetDir = Path.GetDirectoryName(logoFullPath)!;
        var logoBaseName = Path.GetFileNameWithoutExtension(logoBase);
        var logoExt = Path.GetExtension(logoBase);

        // 1. Try each preferred target size in order (_altform-unplated first)
        foreach (var size in targetSizes)
        {
            var targetPathUnplated
                = Path.Combine(assetDir, $"{logoBaseName}.targetsize-{size}_altform-unplated{logoExt}");
            if (File.Exists(targetPathUnplated))
            {
                return targetPathUnplated;
            }

            var targetPath = Path.Combine(assetDir, $"{logoBaseName}.targetsize-{size}{logoExt}");
            if (File.Exists(targetPath))
            {
                return targetPath;
            }
        }

        // 2. Try highest scale: _altform-unplated after .scale-XX, then regular
        if (Directory.Exists(assetDir))
        {
            var files = Directory.GetFiles(assetDir, $"{logoBaseName}.*{logoExt}");

            static bool IsUnplatedScale(string f)
            {
                return Regex.IsMatch(f, @"\.scale-(\d+)_altform-unplated", RegexOptions.IgnoreCase);
            }

            static bool IsRegularScale(string f)
            {
                return Regex.IsMatch(f, @"\.scale-(\d+)(?!_altform-unplated)", RegexOptions.IgnoreCase);
            }

            static int ExtractScale(string f)
            {
                var m = Regex.Match(f, @"\.scale-(\d+)", RegexOptions.IgnoreCase);
                return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            }

            var bestAltUnplated = files
                .Where(static f => IsUnplatedScale(f))
                .Select(static f => (Path: f, Scale: ExtractScale(f)))
                .OrderByDescending(static x => x.Scale)
                .Select(static x => x.Path)
                .FirstOrDefault();
            if (bestAltUnplated is not null)
            {
                return bestAltUnplated;
            }

            var bestScaleFile = files
                .Where(static f => IsRegularScale(f))
                .Select(static f => (Path: f, Scale: ExtractScale(f)))
                .OrderByDescending(static x => x.Scale)
                .Select(static x => x.Path)
                .FirstOrDefault();
            if (bestScaleFile is not null)
            {
                return bestScaleFile;
            }

            // 3. Try "raw" _altform-unplated and plain assets (no targetsize/scale)
            var baseUnplated = Path.Combine(assetDir, $"{logoBaseName}_altform-unplated{logoExt}");
            if (File.Exists(baseUnplated))
            {
                return baseUnplated;
            }

            var baseRaw = Path.Combine(assetDir, $"{logoBaseName}{logoExt}");
            if (File.Exists(baseRaw))
            {
                return baseRaw;
            }
        }

        // 4. Fallback to manifest base asset path itself (full path)
        if (File.Exists(logoFullPath))
        {
            return logoFullPath;
        }

        return null;
    }
}