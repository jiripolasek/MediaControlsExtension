// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Services;

internal static class GsmtcSessionCorrelation
{
    internal static bool IsSameSource(
        GlobalSystemMediaTransportControlsSession left,
        GlobalSystemMediaTransportControlsSession right)
    {
        GsmtcOperationGate.VerifyAccess();

        return string.Equals(
            left.SourceAppUserModelId,
            right.SourceAppUserModelId,
            StringComparison.Ordinal);
    }

    internal static int FindSameSource(
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions,
        GlobalSystemMediaTransportControlsSession target)
    {
        GsmtcOperationGate.VerifyAccess();

        var targetAppId = target.SourceAppUserModelId;
        for (var index = 0; index < sessions.Count; index++)
        {
            if (string.Equals(
                sessions[index].SourceAppUserModelId,
                targetAppId,
                StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }
}