// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Diagnostics;
using Shmuelie.WinRTServer;
using Shmuelie.WinRTServer.CsWinRT;

namespace JPSoftworks.MediaControlsExtension;

internal static class Program
{
    [MTAThread]
    public static async Task Main(string[] args)
    {
        Logger.Initialize("JPSoftworks", "MediaControlsExtension");

        if (args.Length > 0 && args[0] == "-RegisterProcessAsComServer")
        {
            // Run with low priority
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Idle;
            // This is a workaround for the process efficiency mode issue in Windows 11.
            EfficiencyModeHelper.TryEnableProcessEfficiencyMode();

            ComServer server = new();
            ManualResetEvent extensionDisposedEvent = new(false);

            // We are instantiating an extension instance once above, and returning it every time the callback in RegisterExtension below is called.
            // This makes sure that only one instance of SampleExtension is alive, which is returned every time the host asks for the IExtension object.
            // If you want to instantiate a new instance each time the host asks, create the new instance inside the delegate.
            MediaControlsExtension extensionInstance = new(extensionDisposedEvent);
            server.RegisterClass<MediaControlsExtension, IExtension>(() => extensionInstance);
            server.Start();

            // This will make the main thread wait until the event is signalled by the extension class.
            // Since we have single instance of the extension object, we exit as soon as it is disposed.
            extensionDisposedEvent.WaitOne();

            server.UnsafeDispose();
        }
        else
        {
            await StartupHelper.HandleDirectLaunchAsync();
        }
    }
}