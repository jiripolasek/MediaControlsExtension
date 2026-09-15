using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting.Tests;

internal sealed class ReviewActivationPeer : TestWorkerRpc
{
    public static async Task RunAsync(string pipeName, Guid owner, int processId)
    {
        await using var peer = new ReviewActivationPeer();
        await RunAsync(pipeName, processId, peer).ConfigureAwait(false);
    }

    public override async Task<MediaBackendCommandResult> ExecuteAsync(
        WorkerCommand request,
        CancellationToken cancellationToken)
    {
        if (request.Command.Operation == MediaOperation.ActivateSource)
        {
            await this.PublishAsync("Admitted").ConfigureAwait(false);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }

            var command = request.Command;
            var late = new WorkerActivation(this.Epoch, new HostedSourceActivation(request.CommandId,
                new MediaBackendSessionTarget(command.SessionId, command.BindingGeneration), "spike.player",
                "Initial"));
            HostingTests.Check(
                !await this.Owner.ActivateSourceAsync(late, CancellationToken.None).ConfigureAwait(false),
                "A canceled command activated its source.");
            _ = this.SendLateAsync(late);
        }

        return new MediaBackendCommandResult(MediaBackendCommandStatus.Completed, null);
    }

    private async Task SendLateAsync(WorkerActivation late)
    {
        await Task.Delay(100).ConfigureAwait(false);
        await this.Owner.ActivateSourceAsync(late, CancellationToken.None).ConfigureAwait(false);
    }
}