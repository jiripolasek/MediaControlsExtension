using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Threading.Channels;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting;

/// <summary>Serves a compiled leaf backend over one owner-bound connection.</summary>
public static class MediaBackendHost
{
    internal static Task<int> RunAsync(string pipeName, Guid owner, int ownerProcessId, string backendId,
        Func<IMediaBackend> factory, TimeSpan? cleanupTimeout = null) =>
        RunAsync(pipeName, owner, ownerProcessId, backendId, _ => factory(), cleanupTimeout);

    /// <summary>Hosts one backend until the owner disconnects; the executable must exit after this method returns.</summary>
    /// <param name="pipeName">Private pipe created by the owning extension.</param>
    /// <param name="owner">Fresh owner token for this launch.</param>
    /// <param name="ownerProcessId">Expected server PID, verified through the connected pipe handle.</param>
    /// <param name="backendId">Compiled factory ID that must match the owner's handshake.</param>
    /// <param name="factory">Creates one owned backend after a validated handshake; no factory work runs on the pipe reader.</param>
    /// <param name="cleanupTimeout">Separate drain and disposal budgets; defaults to two seconds each.</param>
    /// <returns>Zero after graceful cleanup; two when cleanup could not finish. Handshake failures throw.</returns>
    public static async Task<int> RunAsync(string pipeName, Guid owner, int ownerProcessId, string backendId,
        Func<HostedBackendContext, IMediaBackend> factory, TimeSpan? cleanupTimeout = null)
    {
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(startup.Token).ConfigureAwait(false);
        PipeProtocol.VerifyPeer(pipe, ownerProcessId, server: false);
        using var protocol = new PipeProtocol(pipe);
        var hello = await protocol.ReadAsync(startup.Token).ConfigureAwait(false);
        if (hello.Kind != MessageKind.Hello || hello.Version != PipeProtocol.Version || hello.Owner != owner ||
            hello.Epoch != Guid.Empty || hello.BackendId != backendId || hello.Policy is null ||
            new[] { hello.RequestTimeout, hello.ObservationTimeout, hello.PolicyTimeout }
                .Any(static duration => duration <= TimeSpan.Zero || duration > TimeSpan.FromMilliseconds(uint.MaxValue - 1)))
        {
            throw new InvalidDataException("Invalid worker handshake.");
        }

        protocol.WriteTimeout = hello.RequestTimeout;
        var epoch = Guid.NewGuid();
        await protocol.WriteAsync(new(MessageKind.Welcome, owner, epoch) { CanActivateSource = hello.CanActivateSource }, startup.Token).ConfigureAwait(false);
        using var server = new Server(protocol, owner, epoch, factory, hello, cleanupTimeout ?? TimeSpan.FromSeconds(2));
        return await server.RunAsync().ConfigureAwait(false);
    }

    private sealed class Server(PipeProtocol protocol, Guid owner, Guid epoch, Func<HostedBackendContext, IMediaBackend> factory,
        WireMessage hello, TimeSpan cleanupTimeout) : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _observations = new(1, 1);
        private readonly SemaphoreSlim _artworkTransfers = new(1, 1);
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<bool> _refresh = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        private readonly ConcurrentDictionary<long, Request> _requests = new();
        private readonly ConcurrentDictionary<long, TaskCompletionSource<bool>> _callbacks = new();
        private HostedBackendContext? _context;
        private IMediaBackend? _backend;
        private bool _drained;
        private long _appliedPolicyRevision;
        private int _artworkRequests;

        public async Task<int> RunAsync()
        {
            var initialize = Task.Run(() => this.GuardAsync(this.InitializeAsync));
            var watch = Task.Run(() => this.GuardAsync(this.WatchAsync));
            var snapshots = Task.Run(() => this.GuardAsync(this.PublishSnapshotsAsync));
            try
            {
                while (true)
                {
                    var message = await protocol.ReadAsync(this._stop.Token).ConfigureAwait(false);
                    if (message.Owner != owner || message.Epoch != epoch || message.Version != PipeProtocol.Version)
                    {
                        throw new InvalidDataException("A message belongs to a different worker lifetime.");
                    }

                    if (message.Kind == MessageKind.Shutdown)
                    {
                        break;
                    }

                    if (message.Kind == MessageKind.Cancel)
                    {
                        if (this._requests.TryGetValue(message.RequestId, out var request))
                        {
                            _ = CancelAsync(request.Cancellation);
                        }

                        continue;
                    }

                    if (message.Kind == MessageKind.ActivationReply)
                    {
                        if (this._callbacks.TryGetValue(message.RequestId, out var callback))
                        {
                            callback.TrySetResult(message.Error is null && message.Activated);
                        }

                        continue;
                    }

                    if (message.Kind is not (MessageKind.Execute or MessageKind.Artwork or MessageKind.Invalidate or MessageKind.Policy) ||
                        message.RequestId <= 0 || this._requests.Count >= 32 ||
                        message.Kind == MessageKind.Artwork && Volatile.Read(ref this._artworkRequests) >= PipeProtocol.MaximumArtworkRequests)
                    {
                        throw new InvalidDataException("Invalid request or worker admission limit exceeded.");
                    }

                    var pending = new Request(CancellationTokenSource.CreateLinkedTokenSource(this._stop.Token), message.Kind);
                    if (!this._requests.TryAdd(message.RequestId, pending))
                    {
                        pending.Cancellation.Dispose();
                        throw new InvalidDataException("Duplicate worker request ID.");
                    }

                    if (message.Kind == MessageKind.Artwork) { Interlocked.Increment(ref this._artworkRequests); }
                    pending.Task = Task.Run(() => this.HandleAsync(message, pending.Cancellation.Token));
                    _ = this.EnforceRequestDeadlineAsync(pending);
                }
            }
            catch (Exception)
            {
                // Losing the owner connection ends this worker lifetime.
            }
            finally
            {
                protocol.Dispose();
                this._ready.TrySetCanceled();
            }

            var cancel = CancelAsync(this._stop);
            var operations = this._requests.Values.Select(static request => request.Task).ToArray();
            try
            {
                await Task.WhenAll(operations.Append(initialize).Append(watch).Append(snapshots).Append(cancel))
                    .WaitAsync(cleanupTimeout).ConfigureAwait(false);
                this._drained = true;
                if (this._backend is { } backend)
                {
                    await Task.Run(async () => await backend.DisposeAsync().ConfigureAwait(false))
                        .WaitAsync(cleanupTimeout).ConfigureAwait(false);
                }

                return 0;
            }
            catch (Exception)
            {
                // The executable must exit even when native cleanup cannot finish.
                return 2;
            }
        }

        public void Dispose()
        {
            if (this._drained)
            {
                this._stop.Dispose();
                this._observations.Dispose();
                this._artworkTransfers.Dispose();
            }
        }

        private async Task InitializeAsync()
        {
            this._context = new(hello.CanActivateSource, hello.Logging, this.ActivateAsync);
            this._backend = factory(this._context);
            await this.ApplyPolicyAsync(hello.Policy!.ToPolicy(), this._stop.Token).ConfigureAwait(false);
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
                    deadline.CancelAfter(hello.ObservationTimeout);
                    using var close = deadline.Token.Register(protocol.Dispose);
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
                            recovery.CancelAfter(hello.ObservationTimeout);
                            recovering = true;
                        }
                        await protocol.WriteAsync(new(MessageKind.SnapshotFailure, owner, epoch)
                        {
                            Error = ex.Message,
                            SourcePolicyRevision = this._appliedPolicyRevision,
                        }, this._stop.Token).ConfigureAwait(false);
                        retry = true;
                    }

                    if (!retry && snapshot is null) { throw new InvalidDataException("The backend returned no snapshot."); }
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
                        await protocol.WriteAsync(new(MessageKind.Snapshot, owner, epoch) { Snapshot = snapshot }, this._stop.Token).ConfigureAwait(false);
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

        private async Task HandleAsync(WireMessage message, CancellationToken cancellationToken)
        {
            var reply = new WireMessage(MessageKind.Reply, owner, epoch, message.RequestId);
            try
            {
                await this._ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                switch (message.Kind)
                {
                    case MessageKind.Execute:
                        var command = message.Command ?? throw new InvalidDataException("Missing command.");
                        if (!command.SessionsToPause.IsEmpty || !Enum.IsDefined(command.Operation))
                        {
                            throw new InvalidDataException("Invalid leaf command.");
                        }

                        reply = reply with { Result = await this._context!.ExecuteAsync(this._backend!, message.RequestId,
                            command, cancellationToken).ConfigureAwait(false) };
                        break;
                    case MessageKind.Artwork:
                        reply = await this.SendArtworkAsync(message, cancellationToken).ConfigureAwait(false);
                        break;
                    case MessageKind.Invalidate:
                        if (message.Invalidations.IsDefault)
                        {
                            throw new InvalidDataException("Invalid observation request array.");
                        }

                        this._backend!.InvalidateObservations(message.Invalidations);
                        break;
                    case MessageKind.Policy:
                        await this._observations.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            await this.ApplyPolicyAsync(message.Policy?.ToPolicy()
                                ?? throw new InvalidDataException("Missing policy."), cancellationToken).ConfigureAwait(false);
                            reply = reply with { Policy = message.Policy };
                        }
                        finally
                        {
                            this._observations.Release();
                        }

                        break;
                }

            }
            catch (Exception ex)
            {
                reply = reply with { Error = ex.Message };
            }

            if (message.Kind is MessageKind.Execute or MessageKind.Invalidate or MessageKind.Policy)
            {
                this._refresh.Writer.TryWrite(true);
            }

            if (this._requests.TryRemove(message.RequestId, out var completed))
            {
                if (completed.Kind == MessageKind.Artwork) { Interlocked.Decrement(ref this._artworkRequests); }
                completed.Cancellation.Dispose();
            }

            try
            {
                await protocol.WriteAsync(reply, this._stop.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                protocol.Dispose();
            }
        }

        private async Task EnforceRequestDeadlineAsync(Request request)
        {
            try
            {
                await this._ready.Task.WaitAsync(this._stop.Token).ConfigureAwait(false);
                await request.Task.WaitAsync(WorkerTimeouts.ForRequest(request.Kind,
                    hello.RequestTimeout, hello.ObservationTimeout, hello.PolicyTimeout)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                protocol.Dispose();
            }
        }

        private async Task<WireMessage> SendArtworkAsync(WireMessage message, CancellationToken cancellationToken)
        {
            var key = message.ArtworkKey ?? throw new InvalidDataException("Missing artwork key.");
            var artwork = await this._backend!.GetArtworkAsync(key, cancellationToken).ConfigureAwait(false);
            if (artwork is not null && artwork.Data.Length > PipeProtocol.MaximumArtworkBytes)
            {
                throw new InvalidDataException("Artwork exceeds the supported size limit.");
            }

            await this._artworkTransfers.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await protocol.WriteAsync(new(MessageKind.ArtworkStart, owner, epoch, message.RequestId)
                {
                    ArtworkKey = key,
                    Artwork = artwork is null ? null : artwork with { Data = ReadOnlyMemory<byte>.Empty },
                    ArtworkLength = artwork?.Data.Length ?? 0,
                }, this._stop.Token).ConfigureAwait(false);
                if (artwork is not null)
                {
                    for (var offset = 0; offset < artwork.Data.Length; offset += PipeProtocol.MaximumChunkBytes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await protocol.WriteAsync(new(MessageKind.ArtworkChunk, owner, epoch, message.RequestId)
                        {
                            Offset = offset,
                            Chunk = artwork.Data.Slice(offset, Math.Min(PipeProtocol.MaximumChunkBytes, artwork.Data.Length - offset)).ToArray(),
                        }, this._stop.Token).ConfigureAwait(false);
                        await Task.Yield();
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                return new(MessageKind.ArtworkEnd, owner, epoch, message.RequestId)
                {
                    ArtworkKey = key,
                };
            }
            finally
            {
                this._artworkTransfers.Release();
            }
        }

        private async Task<bool> ActivateAsync(HostedSourceActivation activation, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!this._callbacks.TryAdd(activation.CommandId, completion))
            {
                throw new InvalidOperationException("This activation already has a callback.");
            }

            try
            {
                await protocol.WriteAsync(new(MessageKind.ActivateSource, owner, epoch, activation.CommandId)
                {
                    Activation = activation,
                }, cancellationToken, this._stop.Token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this._stop.Token);
                return await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            finally
            {
                this._callbacks.TryRemove(activation.CommandId, out _);
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

        private async Task SendFaultAsync(string error)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                using var close = timeout.Token.Register(protocol.Dispose);
                await protocol.WriteAsync(new(MessageKind.Fault, owner, epoch) { Error = error }, timeout.Token).ConfigureAwait(false);
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

        private sealed class Request(CancellationTokenSource cancellation, MessageKind kind)
        {
            public MessageKind Kind { get; } = kind;
            public CancellationTokenSource Cancellation { get; } = cancellation;
            public Task Task { get; set; } = Task.CompletedTask;
        }
    }
}