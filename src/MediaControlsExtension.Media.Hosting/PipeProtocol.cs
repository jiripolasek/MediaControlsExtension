using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting;

internal static partial class PipeProtocol
{
    internal const int Version = 7;
    internal const int MaximumFrameBytes = 16 * 1024 * 1024;
    internal const int MaximumArtworkBytes = 32 * 1024 * 1024;
    internal const int MaximumChunkBytes = 64 * 1024;
    internal const int MaximumArtworkRequests = 2;

    public static void VerifyPeer(PipeStream pipe, int expectedProcessId, bool server)
    {
        uint processId;
        var result = server
            ? GetNamedPipeClientProcessId(pipe.SafePipeHandle, out processId)
            : GetNamedPipeServerProcessId(pipe.SafePipeHandle, out processId);
        if (result == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        if (processId != expectedProcessId)
        {
            throw new InvalidDataException("The worker pipe belongs to a different process.");
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}