using System.Collections.Immutable;
using System.IO.Pipes;
using System.Threading.Channels;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting.Tests;

internal class TestWorkerRpc : IWorkerRpc, IWorkerNotifications, IAsyncDisposable
{
    public WorkerRpcEndpoint Endpoint { get; private set; } = null!;
    public IOwnerRpc Owner { get; private set; } = null!;
    public WorkerHello Hello { get; private set; } = null!;
    public Guid Epoch { get; } = Guid.NewGuid();
    public SyntheticBackend Backend { get; } = new("activation", new(true, null, (_, _) => Task.FromResult(true)));
    public MediaBackendSnapshot Snapshot { get; private set; } = null!;
    public ValueTask DisposeAsync() => this.Backend.DisposeAsync();

    public void Begin(Guid epoch) => _ = this.PublishAsync();
    public void Shutdown(Guid epoch) => this.Endpoint.Dispose();

    public Task<WorkerWelcome> InitializeAsync(WorkerHello hello, CancellationToken cancellationToken)
    {
        this.Hello = hello;
        this.Endpoint.Handler.WriteTimeout = hello.RequestTimeout;
        return Task.FromResult(
            new WorkerWelcome(PipeProtocol.Version, hello.Owner, this.Epoch, hello.CanActivateSource));
    }

    public virtual Task<MediaBackendCommandResult> ExecuteAsync(
        WorkerCommand request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new MediaBackendCommandResult(MediaBackendCommandStatus.Completed, null));

    public virtual Task<long> ApplyPolicyAsync(
        Guid epoch,
        SourcePolicyMessage policy,
        CancellationToken cancellationToken) => Task.FromResult(policy.Revision);

    public virtual Task InvalidateAsync(
        Guid epoch,
        ImmutableArray<MediaBackendObservationRequest> requests,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public virtual Task<WorkerArtwork?> CopyArtworkAsync(
        Guid epoch,
        MediaArtworkKey key,
        Stream destination,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public static async Task RunAsync(
        string pipeName,
        int processId,
        TestWorkerRpc target,
        Func<Stream, Stream>? wrap = null)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        PipeProtocol.VerifyPeer(pipe, processId, false);
        var multiplexing = await MultiplexingStream.CreateAsync(pipe,
            new MultiplexingStream.Options
            {
                ProtocolMajorVersion = 2, DefaultChannelReceivingWindowSize = PipeProtocol.MaximumChunkBytes
            }, deadline.Token).ConfigureAwait(false);
        var channel = await multiplexing.AcceptChannelAsync("rpc", cancellationToken: deadline.Token)
            .ConfigureAwait(false);
        var stream = channel.AsStream();
        await using var endpoint = new WorkerRpcEndpoint(multiplexing,
            new WorkerMessageHandler(wrap?.Invoke(stream) ?? stream, WorkerRpcEndpoint.CreateFormatter(multiplexing)));
        target.Endpoint = endpoint;
        target.Owner = endpoint.Rpc.Attach<IOwnerRpc>();
        endpoint.Rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IWorkerRpc>(), target, null);
        endpoint.Rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IWorkerNotifications>(), target, null);
        endpoint.Rpc.StartListening();
        await endpoint.Rpc.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public async Task PublishAsync(string? title = null, long? policyRevision = null)
    {
        this.Snapshot ??= await this.Backend.ReadSnapshotAsync(default).ConfigureAwait(false);
        var snapshot = this.Snapshot;
        this.Snapshot = snapshot with
        {
            Revision = snapshot.Revision + 1,
            SourcePolicyRevision = policyRevision ?? snapshot.SourcePolicyRevision,
            Sessions =
            [
                snapshot.Sessions[0] with
                {
                    MediaProperties = snapshot.Sessions[0].MediaProperties with
                    {
                        Title = title ?? snapshot.Sessions[0].MediaProperties.Title
                    }
                }
            ]
        };
        await this.Endpoint.Rpc
            .NotifyAsync(nameof(IOwnerNotifications.Snapshot), new WorkerSnapshot(this.Epoch, this.Snapshot))
            .ConfigureAwait(false);
    }
}

internal sealed class TestOwnerRpc : IOwnerNotifications, IOwnerRpc
{
    public Channel<WorkerSnapshot> Snapshots { get; } = Channel.CreateUnbounded<WorkerSnapshot>();
    public void Snapshot(WorkerSnapshot notice) => this.Snapshots.Writer.TryWrite(notice);
    public void SnapshotFailed(WorkerSnapshotFailure failure) { }
    public Task ReportFaultAsync(WorkerFault fault, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<bool> ActivateSourceAsync(WorkerActivation request, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}