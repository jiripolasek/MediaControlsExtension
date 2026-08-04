// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Services;

internal static class DetailedLoggingMode
{
    // Process-local by design; explicit debug launches seed this as enabled.
    private static int _enabled;

    public static bool IsEnabled
        => Volatile.Read(ref _enabled) != 0;

    public static void InitializeForProcess(bool enabled)
    {
        Volatile.Write(ref _enabled, enabled ? 1 : 0);
    }

    public static bool ShouldWriteToFile(LogLevel logLevel)
    {
        return logLevel is >= LogLevel.Trace and < LogLevel.None &&
               (IsEnabled || logLevel >= LogLevel.Information);
    }

    public static bool Toggle()
    {
        int currentState;
        int newState;
        do
        {
            currentState = Volatile.Read(ref _enabled);
            newState = currentState == 0 ? 1 : 0;
        }
        while (Interlocked.CompareExchange(
                   ref _enabled,
                   newState,
                   currentState) != currentState);

        return newState != 0;
    }
}
