using JPSoftworks.MediaControlsExtension.Media.Hosting;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using Microsoft.Extensions.Logging;

namespace JPSoftworks.MediaControlsExtension.MediaHost;

/// <summary>Creates a fresh backend after the owner handshake; the host owns its lifetime.</summary>
/// <param name="context">Owner capabilities and callbacks; activation requires an admitted activation command.</param>
/// <param name="loggerFactory">Worker-owned logging, valid through backend disposal.</param>
internal delegate IMediaBackend WorkerBackendFactory(HostedBackendContext context, ILoggerFactory loggerFactory);