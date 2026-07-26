// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Media.Infrastructure.Gsmtc;

internal static class GsmtcErrors
{
    private const int EBounds = unchecked((int)0x8000000B);
    private const int RoEClosed = unchecked((int)0x80000013);

    public static bool IndicatesStaleSession(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is ObjectDisposedException ||
               exception.HResult is EBounds or RoEClosed;
    }
}