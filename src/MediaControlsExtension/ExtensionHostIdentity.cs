// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension;

internal static class ExtensionHostIdentity
{
    public const string PublisherMoniker = "JPSoftworks";

    public const string ProductMoniker = "MediaControlsExtension";

    public static string GetLogDirectoryPath()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("The local application data directory is unavailable.");
        }

        return Path.GetFullPath(
            Path.Combine(
                localApplicationData,
                PublisherMoniker,
                ProductMoniker));
    }
}
