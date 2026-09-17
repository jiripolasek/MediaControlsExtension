// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Runtime.InteropServices;

namespace JPSoftworks.MediaControlsExtension.Media.Gsmtc;

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

    public static Task DelayUntilTimeoutAsync(
        TimeSpan timeout,
        TimeSpan resumeGraceDelay,
        CancellationToken cancellationToken)
    {
        return DelayUntilTimeoutAsync(timeout, resumeGraceDelay, GetTime, Task.Delay, cancellationToken);
    }

    internal static async Task DelayUntilTimeoutAsync(
        TimeSpan timeout,
        TimeSpan resumeGraceDelay,
        Func<TimeSpan> getTime,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        var started = getTime();
        while (true)
        {
            var remaining = timeout - (getTime() - started);
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await delay(remaining, cancellationToken).ConfigureAwait(false);
        }

        await delay(resumeGraceDelay, cancellationToken).ConfigureAwait(false);
    }
}