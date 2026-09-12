// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Globalization;
using JPSoftworks.MediaControlsExtension.Media;
using JPSoftworks.MediaControlsExtension.Resources;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal static class MediaCommandFeedback
{
    public static string AppendPauseWarning(string message, MediaCommandOutcome outcome)
    {
        var warning = GetPauseWarning(outcome);
        return warning is null ? message : $"{message} {warning}";
    }

    public static string? GetPauseWarning(MediaCommandOutcome outcome)
    {
        if (outcome.Status != MediaCommandOutcomeStatus.Completed || outcome.PauseOutcomes.IsDefaultOrEmpty)
        {
            return null;
        }

        var failures = outcome.PauseOutcomes.Count(static pause =>
            pause.Status is MediaCommandOutcomeStatus.Failed or MediaCommandOutcomeStatus.Unavailable);
        if (failures == 0)
        {
            return null;
        }

        var key = failures == 1 ? "Toast_PauseOtherFailed" : "Toast_PauseOthersFailed";
        return string.Format(CultureInfo.CurrentCulture, Strings.ResourceManager.GetString(key, Strings.Culture)!, failures);
    }
}