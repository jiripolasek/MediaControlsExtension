// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace JPSoftworks.MediaControlsExtension.Media.Browser;

internal static partial class BrowserForegroundPermission
{
    internal static bool AllowRelay(NamedPipeClientStream pipe) =>
        GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId) && processId != 0 &&
        AllowSetForegroundWindow(processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);
}