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
    public static string? GetWarning(MediaCommandOutcome outcome) => outcome.Status switch
    {
        MediaCommandOutcomeStatus.Completed => GetPauseWarning(outcome),
        _ => GetFailureMessage(outcome.Status),
    };

    public static string? GetFailureMessage(MediaCommandOutcomeStatus status) => status switch
    {
        MediaCommandOutcomeStatus.Completed => null,
        MediaCommandOutcomeStatus.Superseded or MediaCommandOutcomeStatus.Canceled => null,
        MediaCommandOutcomeStatus.Unconfirmed => Strings.Toast_PlaybackUnconfirmed,
        MediaCommandOutcomeStatus.Abandoned => Strings.Toast_PlaybackAbandoned,
        MediaCommandOutcomeStatus.SessionGone => $"\U0001F622 {Strings.Toast_NoCurrentSession}",
        MediaCommandOutcomeStatus.Unsupported => $"\U0001F6AB {Strings.Toast_NothingHappened}",
        _ => $"\U0001F622 {Strings.Toast_NothingHappened}",
    };

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
            pause.Status is MediaCommandOutcomeStatus.Failed or MediaCommandOutcomeStatus.Unavailable or MediaCommandOutcomeStatus.Unconfirmed);
        if (failures == 0)
        {
            return null;
        }

        var key = failures == 1 ? "Toast_PauseOtherFailed" : "Toast_PauseOthersFailed";
        return string.Format(CultureInfo.CurrentCulture, Strings.ResourceManager.GetString(key, Strings.Culture)!, failures);
    }
}