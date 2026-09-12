// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Media.Gsmtc;

/// <summary>Provides host window activation for a validated GSMTC session binding.</summary>
public interface IGsmtcSourceActivator
{
    Task<bool> TryActivateAsync(string applicationId, string mediaTitle, CancellationToken cancellationToken);
}