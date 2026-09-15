using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO.Pipes;
using System.Threading.Channels;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using StreamJsonRpc;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting;

/// <summary>Serves a compiled leaf backend over one owner-bound connection.</summary>
public static class MediaBackendHost
{
    internal static Task<int> RunAsync(
        string pipeName,
        Guid owner,
        int ownerProcessId,
        string backendId,
        Func<IMediaBackend> factory,
        TimeSpan? cleanupTimeout = null) =>
        RunAsync(pipeName, owner, ownerProcessId, backendId, _ => factory(), cleanupTimeout);

    /// <summary>Hosts one backend until the owner disconnects; the executable must exit after this method returns.</summary>
    /// <param name="pipeName">Private pipe created by the owning extension.</param>
    /// <param name="owner">Fresh owner token for this launch.</param>
    /// <param name="ownerProcessId">Expected server PID, verified through the connected pipe handle.</param>
    /// <param name="backendId">Compiled factory ID that must match the owner's handshake.</param>
    /// <param name="factory">Creates one owned backend after a validated handshake; no factory work runs on the pipe reader.</param>
    /// <param name="cleanupTimeout">Separate drain and disposal budgets; defaults to two seconds each.</param>
    /// <param name="reportFailure">Optional local watchdog diagnostic sink; runs after connection closure, may overlap cleanup, and ignores sink exceptions.</param>
    /// <returns>Zero after graceful cleanup; two when cleanup could not finish. Handshake failures throw.</returns>
    public static async Task<int> RunAsync(
        string pipeName,
        Guid owner,
        int ownerProcessId,
        string backendId,
        Func<HostedBackendContext, IMediaBackend> factory,
        TimeSpan? cleanupTimeout = null,
        Action<string>? reportFailure = null)
    {
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(startup.Token).ConfigureAwait(false);
        PipeProtocol.VerifyPeer(pipe, ownerProcessId, false);
        await using var endpoint
            = await WorkerRpcEndpoint.CreateAsync(pipe, false, startup.Token).ConfigureAwait(false);
        using var server = new Server(endpoint, owner, backendId, factory, cleanupTimeout ?? TimeSpan.FromSeconds(2),
            reportFailure);
        endpoint.Rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IWorkerRpc>(), server, null);
        endpoint.Rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IWorkerNotifications>(), server, null);
        server.Owner = endpoint.Rpc.Attach<IOwnerRpc>();
        endpoint.Rpc.StartListening();
        return await server.RunAsync(startup.Token).ConfigureAwait(false);
    }

    private sealed class Server(
        WorkerRpcEndpoint protocol,
        Guid owner,
        string backendId,
        Func<HostedBackendContext, IMediaBackend> factory,
        TimeSpan cleanupTimeout,
        Action<string>? reportFailure) : IWorkerRpc, IWorkerNotifications, IDisposable
    {
        private readonly SemaphoreSlim _artworkTransfers = new(1, 1);
        private readonly TaskCompletionSource _begin = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Guid _epoch = Guid.NewGuid();
        private readonly SemaphoreSlim _observations = new(1, 1);
        private readonly Lock _operationGate = new();
        private readonly ConcurrentDictionary<long, Task> _operations = new();
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Channel<bool> _refresh = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        private readonly CancellationTokenSource _stop = new();
        private long _appliedPolicyRevision;
        private IMediaBackend? _backend;
        private HostedBackendContext? _context;
        private bool _drained;
        private WorkerHello? _hello;
        private long _nextOperation;
        private bool _stopping;
        private int _watchdogExpired;
        public IOwnerRpc Owner { get; set; } = null!;

        public void Dispose()
        {
            if (this._drained)
            {
                this._stop.Dispose();
                this._observations.Dispose();
                this._artworkTransfers.Dispose();
            }
        }

        public void Begin(Guid epoch)
        {
            this.Validate(epoch);
            if (!this._begin.TrySetResult()) { protocol.Dispose(); }
        }

        public void Shutdown(Guid epoch)
        {
            this.Validate(epoch);
            protocol.Dispose();
        }

        public Task<WorkerWelcome> InitializeAsync(WorkerHello hello, CancellationToken cancellationToken)
        {
            if (hello is null || hello.Version != PipeProtocol.Version || hello.Owner != owner ||
                hello.BackendId != backendId ||
                hello.Policy?.ExcludedApplicationIds is null || hello.Policy.Revision < 0 ||
                new[] { hello.RequestTimeout, hello.ObservationTimeout, hello.PolicyTimeout }
                    .Any(static duration =>
                        duration <= TimeSpan.Zero || duration > TimeSpan.FromMilliseconds(uint.MaxValue - 1)) ||
                Interlocked.CompareExchange(ref this._hello, hello, null) is not null)
            {
                this._begin.TrySetException(new InvalidDataException("Invalid worker handshake."));
                protocol.Dispose();
                throw new InvalidDataException("Invalid worker handshake.");
            }

            protocol.Handler.WriteTimeout = hello.RequestTimeout;
            return Task.FromResult(new WorkerWelcome(PipeProtocol.Version, owner, this._epoch,
                hello.CanActivateSource));
        }

        public Task<MediaBackendCommandResult> ExecuteAsync(WorkerCommand request, CancellationToken cancellationToken)
        {
            if (request is null)
            {
                protocol.Dispose();
                throw new InvalidDataException("Missing leaf command.");
            }

            this.Validate(request.Epoch);
            return this.RunOperationAsync(async token =>
            {
                var command = request.Command;
                if (request.CommandId <= 0 || command is null || !command.SessionsToPause.IsEmpty ||
                    !Enum.IsDefined(command.Operation))
                {
                    throw new InvalidDataException("Invalid leaf command.");
                }

                return await this._context!.ExecuteAsync(this._backend!, request.CommandId, command, token)
                    .ConfigureAwait(false);
            }, this._hello!.RequestTimeout, "The worker command exceeded RequestTimeout.", true, cancellationToken);
        }

        public Task<long> ApplyPolicyAsync(Guid epoch, SourcePolicyMessage policy, CancellationToken cancellationToken)
        {
            this.Validate(epoch);
            return this.RunOperationAsync(async token =>
            {
                await this._observations.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await this.ApplyPolicyAsync(policy.ToPolicy(), token).ConfigureAwait(false);
                    return policy.Revision;
                }
                finally { this._observations.Release(); }
            }, this._hello!.PolicyTimeout, "The worker policy update exceeded PolicyTimeout.", true, cancellationToken);
        }

        public Task InvalidateAsync(
            Guid epoch,
            ImmutableArray<MediaBackendObservationRequest> requests,
            CancellationToken cancellationToken)
        {
            this.Validate(epoch);
            return this.RunOperationAsync(token =>
                {
                    if (requests.IsDefault) { throw new InvalidDataException("Invalid observation request array."); }

                    this._backend!.InvalidateObservations(requests);
                    return Task.FromResult(true);
                }, this._hello!.RequestTimeout, "The worker observation invalidation exceeded RequestTimeout.", true,
                cancellationToken);
        }

        public async Task<WorkerArtwork?> CopyArtworkAsync(
            Guid epoch,
            MediaArtworkKey key,
            Stream destination,
            CancellationToken cancellationToken)
        {
            await using (destination.ConfigureAwait(false))
            {
                this.Validate(epoch);
                return await this.RunOperationAsync(async token =>
                    {
                        var artwork = await this._backend!.GetArtworkAsync(key, token).ConfigureAwait(false);
                        if (artwork?.Data.Length > PipeProtocol.MaximumArtworkBytes)
                        {
                            throw new InvalidDataException("Artwork exceeds the supported size limit.");
                        }

                        await this._artworkTransfers.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            var data = artwork?.Data ?? ReadOnlyMemory<byte>.Empty;
                            var length = new byte[4];
                            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
                            await destination.WriteAsync(length, token).ConfigureAwait(false);
                            for (var offset = 0; offset < data.Length; offset += PipeProtocol.MaximumChunkBytes)
                            {
                                await destination
                                    .WriteAsync(
                                        data.Slice(offset,
                                            Math.Min(PipeProtocol.MaximumChunkBytes, data.Length - offset)),
                                        token)
                                    .ConfigureAwait(false);
                            }

                            return artwork is null
                                ? null
                                : new WorkerArtwork(artwork.ContentType, data.Length, artwork.Hash);
                        }
                        finally { this._artworkTransfers.Release(); }
                    }, this._hello!.ObservationTimeout, "The worker artwork request exceeded ObservationTimeout.",
                    false,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private void Validate(Guid epoch)
        {
            if (this._hello is null || epoch != this._epoch)
            {
                protocol.Dispose();
                throw new InvalidDataException("The RPC belongs to a different worker lifetime.");
            }
        }

        public async Task<int> RunAsync(CancellationToken startup)
        {
            var first = await Task.WhenAny(this._begin.Task, protocol.Rpc.Completion).WaitAsync(startup)
                .ConfigureAwait(false);
            if (first != this._begin.Task) { throw new IOException("The owner disconnected during the handshake."); }

            await this._begin.Task.WaitAsync(startup).ConfigureAwait(false);
            var initialize = Task.Run(() => this.GuardAsync(this.InitializeAsync), CancellationToken.None);
            var watch = Task.Run(() => this.GuardAsync(this.WatchAsync), CancellationToken.None);
            var snapshots = Task.Run(() => this.GuardAsync(this.PublishSnapshotsAsync), CancellationToken.None);
            await protocol.Rpc.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            protocol.Dispose();
            Task[] operations;
            lock (this._operationGate)
            {
                this._stopping = true;
                operations = [.. this._operations.Values];
            }

            this._ready.TrySetCanceled(CancellationToken.None);
            var cancel = CancelAsync(this._stop);
            try
            {
                await Task.WhenAll(operations.Select(IgnoreFailureAsync).Append(initialize).Append(watch)
                        .Append(snapshots).Append(cancel))
                    .WaitAsync(cleanupTimeout, CancellationToken.None).ConfigureAwait(false);
                this._drained = true;
                if (this._backend is { } backend)
                {
                    await Task.Run(async () => await backend.DisposeAsync().ConfigureAwait(false),
                            CancellationToken.None)
                        .WaitAsync(cleanupTimeout, CancellationToken.None).ConfigureAwait(false);
                }

                return 0;
            }
            catch (Exception)
            {
                // The executable exits even when native cleanup cannot finish.
                return 2;
            }
        }

        private Task<T> RunOperationAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            TimeSpan budget,
            string timeoutMessage,
            bool refresh,
            CancellationToken caller)
        {
            lock (this._operationGate)
            {
                if (this._stopping) { throw new IOException("The worker is stopping."); }

                var id = ++this._nextOperation;
                var task = Task.Run(async () =>
                {
                    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(caller, this._stop.Token);
                    await this._ready.Task.WaitAsync(cancellation.Token).ConfigureAwait(false);
                    cancellation.Token.ThrowIfCancellationRequested();
                    using var deadline = new CancellationTokenSource(budget);
                    using var close = deadline.Token.Register(() => this.CloseOnTimeout(timeoutMessage));
                    try { return await operation(cancellation.Token).ConfigureAwait(false); }
                    finally
                    {
                        if (refresh) { this._refresh.Writer.TryWrite(true); }
                    }
                });
                this._operations.TryAdd(id, task);
                _ = this.ObserveOperationAsync(id, task);
                return task;
            }
        }

        private static async Task IgnoreFailureAsync(Task task) =>
            await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        private async Task ObserveOperationAsync(long id, Task task)
        {
            await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            this._operations.TryRemove(id, out _);
        }

        private Task<bool> ActivateAsync(HostedSourceActivation activation, CancellationToken cancellationToken) =>
            this.Owner.ActivateSourceAsync(new WorkerActivation(this._epoch, activation), cancellationToken);

        private async Task InitializeAsync()
        {
            this._context = new HostedBackendContext(this._hello!.CanActivateSource, this._hello!.Logging,
                this.ActivateAsync);
            this._backend = factory(this._context);
            await this.ApplyPolicyAsync(this._hello!.Policy!.ToPolicy(), this._stop.Token).ConfigureAwait(false);
            await this._backend.StartAsync(this._stop.Token).ConfigureAwait(false);
            this._ready.TrySetResult();
            this._refresh.Writer.TryWrite(true);
        }

        private async Task WatchAsync()
        {
            await this._ready.Task.ConfigureAwait(false);
            await foreach (var signal in this._backend!.WatchAsync(this._stop.Token).ConfigureAwait(false))
            {
                if (signal != MediaBackendSignal.None)
                {
                    this._refresh.Writer.TryWrite(true);
                }
            }

            this._stop.Token.ThrowIfCancellationRequested();
            throw new IOException("The hosted backend stopped monitoring.");
        }

        private async Task PublishSnapshotsAsync()
        {
            await this._ready.Task.ConfigureAwait(false);
            using var recovery = new CancellationTokenSource();
            var lastFailure = string.Empty;
            using var recoveryClose = recovery.Token.Register(() =>
                _ = this.SendFaultAsync($"Snapshot recovery timed out: {Volatile.Read(ref lastFailure)}"));
            var recovering = false;
            var retryDelay = TimeSpan.FromMilliseconds(500);
            await foreach (var _ in this._refresh.Reader.ReadAllAsync(this._stop.Token).ConfigureAwait(false))
            {
                var retry = false;
                await this._observations.WaitAsync(this._stop.Token).ConfigureAwait(false);
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(this._stop.Token);
                    deadline.CancelAfter(this._hello!.ObservationTimeout);
                    using var close = deadline.Token.Register(() =>
                        this.CloseOnTimeout("The worker snapshot read exceeded ObservationTimeout."));
                    MediaBackendSnapshot? snapshot = null;
                    try
                    {
                        snapshot = await this._backend!.ReadSnapshotAsync(deadline.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!deadline.IsCancellationRequested && !protocol.IsClosed)
                    {
                        Volatile.Write(ref lastFailure, ex.Message);
                        if (!recovering)
                        {
                            recovery.CancelAfter(this._hello!.ObservationTimeout);
                            recovering = true;
                        }

                        await protocol.Rpc.NotifyAsync(nameof(IOwnerNotifications.SnapshotFailed),
                                new WorkerSnapshotFailure(this._epoch, this._appliedPolicyRevision, ex.Message))
                            .ConfigureAwait(false);
                        retry = true;
                    }

                    if (!retry && snapshot is null)
                    {
                        throw new InvalidDataException("The backend returned no snapshot.");
                    }

                    if (snapshot is not null)
                    {
                        if (recovering)
                        {
                            recovery.CancelAfter(Timeout.InfiniteTimeSpan);
                            recovering = false;
                        }

                        retryDelay = TimeSpan.FromMilliseconds(500);
                        if (this._backend is not IMediaSourcePolicyBackend)
                        {
                            snapshot = snapshot with { SourcePolicyRevision = this._appliedPolicyRevision };
                        }

                        await protocol.Rpc.NotifyAsync(nameof(IOwnerNotifications.Snapshot),
                            new WorkerSnapshot(this._epoch, snapshot)).ConfigureAwait(false);
                    }
                }
                finally
                {
                    this._observations.Release();
                }

                if (retry)
                {
                    await Task.Delay(retryDelay, this._stop.Token).ConfigureAwait(false);
                    retryDelay = TimeSpan.FromMilliseconds(Math.Min(2000, retryDelay.TotalMilliseconds * 2));
                    this._refresh.Writer.TryWrite(true);
                }
            }
        }

        private async Task ApplyPolicyAsync(MediaBackendSourcePolicy policy, CancellationToken cancellationToken)
        {
            if (this._backend is IMediaSourcePolicyBackend backend)
            {
                await backend.ApplySourcePolicyAsync(policy, cancellationToken).ConfigureAwait(false);
            }
            else if (policy.ExcludedApplicationIds.Count != 0)
            {
                throw new NotSupportedException("The hosted backend does not support source exclusions.");
            }

            this._appliedPolicyRevision = policy.Revision;
        }

        private async Task GuardAsync(Func<Task> operation)
        {
            try
            {
                await operation().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this._ready.TrySetException(ex);
                if (!this._stop.IsCancellationRequested)
                {
                    await this.SendFaultAsync(ex.Message).ConfigureAwait(false);
                }
            }
        }

        private void CloseOnTimeout(string error)
        {
            if (protocol.IsClosed || Interlocked.Exchange(ref this._watchdogExpired, 1) != 0) { return; }

            protocol.Dispose();
            try { reportFailure?.Invoke(error); }
            catch (Exception)
            {
                // Diagnostics cannot escape the watchdog callback.
            }
        }

        private async Task SendFaultAsync(string error)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                using var close = timeout.Token.Register(protocol.Dispose);
                await this.Owner.ReportFaultAsync(new WorkerFault(this._epoch, error), timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Closing the connection also reports a failed worker.
            }
            finally { protocol.Dispose(); }
        }

        private static async Task CancelAsync(CancellationTokenSource cancellation)
        {
            try
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Cancellation callbacks cannot keep the owner channel alive.
            }
        }
    }
}