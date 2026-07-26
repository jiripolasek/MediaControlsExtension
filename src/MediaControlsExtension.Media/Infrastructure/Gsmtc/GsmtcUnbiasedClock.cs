// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Runtime.InteropServices;

namespace JPSoftworks.MediaControlsExtension.Media.Infrastructure.Gsmtc;

internal static partial class GsmtcUnbiasedClock
{
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryUnbiasedInterruptTime(out ulong unbiasedInterruptTime);

    public static TimeSpan GetTime()
    {
        return QueryUnbiasedInterruptTime(out var interruptTime)
            ? TimeSpan.FromTicks((long)interruptTime)
            : TimeSpan.FromMilliseconds(Environment.TickCount64);
    }
}