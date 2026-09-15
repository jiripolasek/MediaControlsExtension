using System.Collections.Immutable;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting.Tests;

internal sealed class ReviewAdmissionPeer : TestWorkerRpc
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _admitted;
    private int _canceled;
    private MediaBackendObservationChanges _invalidations;

    public static async Task RunAsync(string pipeName, Guid owner, int processId)
    {
        await using var peer = new ReviewAdmissionPeer();
        await RunAsync(pipeName, processId, peer).ConfigureAwait(false);
    }

    public override async Task<MediaBackendCommandResult> ExecuteAsync(
        WorkerCommand request,
        CancellationToken cancellationToken)
    {
        if (request.Command.Operation == MediaOperation.Stop)
        {
            await this.PublishAsync($"Admitted {Interlocked.Increment(ref this._admitted)}").ConfigureAwait(false);
            using var registration = cancellationToken.Register(() =>
            {
                if (Interlocked.Increment(ref this._canceled) == 32) { _ = this.ReleaseAsync(); }
            });
            await this._release.Task.ConfigureAwait(false);
        }

        return new MediaBackendCommandResult(MediaBackendCommandStatus.Completed, null);
    }

    public override Task InvalidateAsync(
        Guid epoch,
        ImmutableArray<MediaBackendObservationRequest> requests,
        CancellationToken cancellationToken)
    {
        foreach (var request in requests) { this._invalidations |= request.Changes; }

        return this.PublishAsync($"Invalidated {(int)this._invalidations}");
    }

    private async Task ReleaseAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        this._release.TrySetResult();
        await this.PublishAsync("Released").ConfigureAwait(false);
    }
}