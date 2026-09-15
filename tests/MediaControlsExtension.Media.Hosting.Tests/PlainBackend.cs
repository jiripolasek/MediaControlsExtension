using System.Collections.Immutable;
using JPSoftworks.MediaControlsExtension.Media;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting.Tests;

internal sealed class PlainBackend : IMediaBackend
{
    private readonly SyntheticBackend _backend = new();

    public Task StartAsync(CancellationToken cancellationToken) => this._backend.StartAsync(cancellationToken);
    public IAsyncEnumerable<MediaBackendSignal> WatchAsync(CancellationToken cancellationToken) => this._backend.WatchAsync(cancellationToken);
    public Task<MediaBackendSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken) => this._backend.ReadSnapshotAsync(cancellationToken);
    public void InvalidateObservations(ImmutableArray<MediaBackendObservationRequest> requests) => this._backend.InvalidateObservations(requests);
    public Task<MediaBackendCommandResult> ExecuteAsync(MediaBackendCommand command, CancellationToken cancellationToken) => this._backend.ExecuteAsync(command, cancellationToken);
    public ValueTask<MediaArtworkContent?> GetArtworkAsync(MediaArtworkKey key, CancellationToken cancellationToken) => this._backend.GetArtworkAsync(key, cancellationToken);
    public ValueTask DisposeAsync() => this._backend.DisposeAsync();
}