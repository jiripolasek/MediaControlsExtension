// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Hosting;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal sealed class ITunesSourceActivator(ILogger<ITunesSourceActivator> logger)
{
    public Task<bool> TryActivateAsync(HostedSourceActivation request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var activated = !string.IsNullOrWhiteSpace(request.ExecutablePath)
                && DesktopWindowManager.SwitchToDesktopAppWindow(request.ExecutablePath, request.MediaTitle);
            return Task.FromResult(activated);
        }
        catch (Exception ex)
        {
            ExtensionLog.UnexpectedError(logger, ex);
            return Task.FromResult(false);
        }
    }
}