// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Gsmtc;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal sealed class GsmtcSourceActivator(ILogger<GsmtcSourceActivator> logger) : IGsmtcSourceActivator
{
    public Task<bool> TryActivateAsync(string applicationId, string mediaTitle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var appInfo = AppInfoResolver.Resolve(applicationId);
        return AppWindowHelper.TryBringToFrontAsync(appInfo, mediaTitle, logger, cancellationToken);
    }
}