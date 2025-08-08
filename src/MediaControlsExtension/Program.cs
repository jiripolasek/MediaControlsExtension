// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using JPSoftworks.CommandPalette.Extensions.Toolkit;

namespace JPSoftworks.MediaControlsExtension;

internal static class Program
{
    [MTAThread]
    public static async Task Main(string[] args)
    {
        await ExtensionHostRunner.RunAsync(args, new()
        {
            PublisherMoniker = "JPSoftworks",
            ProductMoniker = "MediaControlsExtension",
            EnableEfficiencyMode = true,
            ExtensionFactories = [
                new DelegateExtensionFactory(extensionDisposedEvent => new MediaControlsExtension(extensionDisposedEvent))
                ]
        });
    }
}