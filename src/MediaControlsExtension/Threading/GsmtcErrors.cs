// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Threading;

/// <summary>
/// Classifies WinRT failures surfaced by GSMTC. E_BOUNDS and RO_E_CLOSED are
/// projected as <see cref="ArgumentOutOfRangeException"/> or
/// <see cref="ObjectDisposedException"/> (with the localized OS text where a
/// parameter name belongs) and mean the underlying session or stream died —
/// not that a caller passed a bad argument.
/// </summary>
internal static class GsmtcErrors
{
    private const int E_BOUNDS = unchecked((int)0x8000000B);
    private const int RO_E_CLOSED = unchecked((int)0x80000013);

    public static bool IndicatesStaleSession(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is ObjectDisposedException
            || exception.HResult is E_BOUNDS or RO_E_CLOSED;
    }
}
