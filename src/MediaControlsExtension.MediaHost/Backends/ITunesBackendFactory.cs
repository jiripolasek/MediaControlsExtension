using JPSoftworks.MediaControlsExtension.Media.Hosting;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.ITunes;
using Microsoft.Extensions.Logging;

namespace JPSoftworks.MediaControlsExtension.MediaHost.Backends;

internal static class ITunesBackendFactory
{
    public static IMediaBackend Create(HostedBackendContext context, ILoggerFactory loggerFactory) =>
        new ITunesBackend(loggerFactory.CreateLogger<ITunesBackend>(),
            activateSource: context.CanActivateSource ? context.TryActivateSourceAsync : null);
}
