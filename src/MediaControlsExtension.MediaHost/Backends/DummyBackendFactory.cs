using JPSoftworks.MediaControlsExtension.Media.Dummy;
using JPSoftworks.MediaControlsExtension.Media.Hosting;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using Microsoft.Extensions.Logging;

namespace JPSoftworks.MediaControlsExtension.MediaHost.Backends;

internal static class DummyBackendFactory
{
    public static IMediaBackend Create(HostedBackendContext context, ILoggerFactory loggerFactory) => new DummyBackend();
}