using JPSoftworks.MediaControlsExtension.Media.Gsmtc;
using JPSoftworks.MediaControlsExtension.Media.Hosting;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using Microsoft.Extensions.Logging;

namespace JPSoftworks.MediaControlsExtension.MediaHost.Backends;

internal static class GsmtcBackendFactory
{
    public static IMediaBackend Create(HostedBackendContext context, ILoggerFactory loggerFactory) =>
        new GsmtcBackend(loggerFactory.CreateLogger<GsmtcBackend>(),
            context.CanActivateSource ? new SourceActivator(context) : null);

    private sealed class SourceActivator(HostedBackendContext context) : IGsmtcSourceActivator
    {
        public Task<bool> TryActivateAsync(string applicationId, string mediaTitle, CancellationToken cancellationToken) =>
            context.TryActivateSourceAsync(applicationId, mediaTitle, cancellationToken);
    }
}