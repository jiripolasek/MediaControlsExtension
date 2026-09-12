// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Media.Gsmtc;

/// <summary>Provides host window activation for a validated GSMTC session binding.</summary>
public interface IGsmtcSourceActivator
{
    /// <summary>Attempts to foreground or launch the application owning the captured session.</summary>
    /// <param name="applicationId">Nonblank Windows application ID from the validated GSMTC binding.</param>
    /// <param name="mediaTitle">Current media title, or empty; a window-selection hint, not an identity.</param>
    /// <param name="cancellationToken">Requests cancellation of activation.</param>
    /// <returns>True when activation succeeds; false when the source cannot be activated.</returns>
    /// <remarks>Called without UI-thread affinity; the adapter owns any required dispatching.</remarks>
    Task<bool> TryActivateAsync(string applicationId, string mediaTitle, CancellationToken cancellationToken);
}