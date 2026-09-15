using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO.Pipelines;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using StreamJsonRpc;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting;

public sealed partial class OutOfProcessMediaBackend
{
    private sealed class Connection(
        WorkerRpcEndpoint protocol,
        Guid owner,
        WorkerOptions options,
        CancellationToken startup)
        : IOwnerRpc, IOwnerNotifications, IAsyncDisposable
    {
        private readonly SemaphoreSlim _admission = new(32, 32);

        private readonly SemaphoreSlim _artworkAdmission
            = new(PipeProtocol.MaximumArtworkRequests, PipeProtocol.MaximumArtworkRequests);

        private readonly CancellationTokenSource _closed = new();
        private readonly ConcurrentDictionary<long, ActivationCommand> _commands = new();
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<MediaBackendSessionId, MediaBackendObservationChanges> _invalidations = [];
        private readonly Lock _requestGate = new();
        private int _active;
        private OutOfProcessMediaBackend _backend = null!;
        private TaskCompletionSource? _capacityAvailable;
        private bool _closing;
        private string? _failure;
        private bool _invalidating;
        private long _nextCommandId;

        public WorkerRpcEndpoint Protocol { get; } = protocol;
        public Guid Owner { get; } = owner;
        public Guid Epoch { get; set; }
        public IWorkerRpc Worker { get; private set; } = null!;
        public Task? Reader { get; private set; }
        public string? Failure => Volatile.Read(ref this._failure);

        public bool IsClosed
        {
            get
            {
                lock (this._requestGate) { return this._closing; }
            }
        }

        public TaskCompletionSource FirstSnapshot { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WorkerHealth Health { get; } = new();
        public long AppliedPolicyRevision { get; set; }

        public async ValueTask DisposeAsync()
        {
            this.Close();
            await this._drained.Task.ConfigureAwait(false);
            await this.Protocol.DisposeAsync().ConfigureAwait(false);
            this._closed.Dispose();
            this._admission.Dispose();
            this._artworkAdmission.Dispose();
        }

        public void Snapshot(WorkerSnapshot notice) => this.AcceptNotice(notice?.Epoch, () =>
        {
            if (!this.IsClosed && this._backend.AcceptSnapshot(this, notice!.Snapshot))
            {
                this.FirstSnapshot.TrySetResult();
            }
        });

        public void SnapshotFailed(WorkerSnapshotFailure failure) =>
            this.AcceptNotice(failure?.Epoch, () => this._backend.AcceptSnapshotFailure(this, failure!));

        public Task ReportFaultAsync(WorkerFault fault, CancellationToken cancellationToken)
        {
            this.AcceptNotice(fault?.Epoch, () => this.RecordFailure(fault!.Error));
            return Task.CompletedTask;
        }

        public Task<bool> ActivateSourceAsync(WorkerActivation request, CancellationToken cancellationToken)
        {
            if (request?.Activation is not { } activation || request.Epoch != this.Epoch)
            {
                this.Close("The activation request is missing or belongs to a different worker lifetime.");
                return Task.FromResult(false);
            }

            if (!this._commands.TryGetValue(activation.CommandId, out var command) ||
                command.Cancellation.IsCancellationRequested)
            {
                return Task.FromResult(false);
            }

            if (activation.Target !=
                new MediaBackendSessionTarget(command.Command.SessionId, command.Command.BindingGeneration) ||
                Interlocked.Exchange(ref command.CallbackUsed, 1) != 0)
            {
                this.Close("The activation request does not match its command or repeats its callback.");
                return Task.FromResult(false);
            }

            return Task.Run(async () =>
            {
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(command.Cancellation,
                    cancellationToken, this._closed.Token);
                try
                {
                    return await this._backend.ActivateAsync(this, activation, cancel.Token).WaitAsync(cancel.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception) { return false; }
            });
        }

        public void Listen(OutOfProcessMediaBackend backend)
        {
            this._backend = backend;
            this.Protocol.Rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IOwnerRpc>(), this, null);
            this.Protocol.Rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IOwnerNotifications>(), this, null);
            this.Worker = this.Protocol.Rpc.Attach<IWorkerRpc>();
            this.Protocol.Rpc.StartListening();
            this.Reader = this.ObserveConnectionAsync();
        }

        private async Task ObserveConnectionAsync()
        {
            try { await this.Protocol.Rpc.Completion.ConfigureAwait(false); }
            catch (Exception ex) { this.RecordFailure(this.Protocol.Handler.Failure?.Message ?? ex.Message); }
            finally
            {
                this.Close(this.Protocol.Rpc.IsDisposed ? null : "The worker disconnected.");
                this.FirstSnapshot.TrySetException(new IOException(this.Failure ?? "The worker disconnected."));
            }
        }

        public Task<long> ApplyPolicyAsync(SourcePolicyMessage policy, CancellationToken cancellationToken) =>
            this.RequestAsync(token => this.Worker.ApplyPolicyAsync(this.Epoch, policy, token), options.PolicyTimeout,
                cancellationToken, duringStartup: !this.FirstSnapshot.Task.IsCompletedSuccessfully);

        public Task<MediaBackendCommandResult> ExecuteAsync(
            MediaBackendCommand command,
            CancellationToken cancellationToken) =>
            this.RequestAsync(async token =>
            {
                var id = Interlocked.Increment(ref this._nextCommandId);
                if (command.Operation == MediaOperation.ActivateSource)
                {
                    this._commands.TryAdd(id, new ActivationCommand(command, token));
                }

                try
                {
                    return await this.Worker.ExecuteAsync(new WorkerCommand(this.Epoch, id, command), token)
                        .ConfigureAwait(false);
                }
                finally { this._commands.TryRemove(id, out _); }
            }, options.RequestTimeout, cancellationToken);

        public Task<bool> InvalidateAsync(ImmutableArray<MediaBackendObservationRequest> requests) =>
            this.RequestAsync(async token =>
            {
                await this.Worker.InvalidateAsync(this.Epoch, requests, token).ConfigureAwait(false);
                return true;
            }, options.RequestTimeout, CancellationToken.None, waitForCapacity: true);

        public Task<MediaArtworkContent?> GetArtworkAsync(MediaArtworkKey key, CancellationToken cancellationToken) =>
            this.RequestAsync(async token =>
            {
                var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 2 * PipeProtocol.MaximumChunkBytes,
                    resumeWriterThreshold: PipeProtocol.MaximumChunkBytes, useSynchronizationContext: false));
                var local = pipe.Reader.AsStream();
                var remote = pipe.Writer.AsStream();
                using (local)
                using (remote)
                using (token.Register(() =>
                       {
                           local.Dispose();
                           remote.Dispose();
                       }))
                {
                    var reply = this.Worker.CopyArtworkAsync(this.Epoch, key, remote, token);
                    var read = this.ReadArtworkAsync(local, token);
                    _ = CloseFailedTransferAsync(reply, local, remote);
                    _ = CloseFailedTransferAsync(read, local, remote);
                    var all = Task.WhenAll(reply, read);
                    try { await all.ConfigureAwait(false); }
                    catch (Exception) when (token.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(token);
                    }
                    catch (Exception) when (read.Exception?.InnerException is EndOfStreamException &&
                                            reply.IsCompletedSuccessfully)
                    {
                        this.Close("The worker sent incomplete artwork.");
                        throw new IOException("The worker sent incomplete artwork.");
                    }

                    var metadata = await reply.ConfigureAwait(false);
                    var bytes = await read.ConfigureAwait(false);
                    if (metadata is null && bytes.Length == 0) { return null; }

                    if (metadata is null || metadata.Length != bytes.Length)
                    {
                        this.Close("Artwork length does not match its metadata.");
                        throw new IOException("Artwork length does not match its metadata.");
                    }

                    return new MediaArtworkContent(metadata.ContentType, bytes, metadata.ContentHash);
                }
            }, options.ObservationTimeout, cancellationToken, true);

        private async Task<byte[]> ReadArtworkAsync(Stream source, CancellationToken cancellationToken)
        {
            var header = new byte[4];
            await source.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadInt32BigEndian(header);
            if (length is < 0 or > PipeProtocol.MaximumArtworkBytes)
            {
                this.Close("Invalid artwork length.");
                throw new IOException("Invalid artwork length.");
            }

            var bytes = new byte[length];
            await source.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (await source.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
            {
                this.Close("The worker sent trailing artwork bytes.");
                throw new IOException("The worker sent trailing artwork bytes.");
            }

            return bytes;
        }

        private static async Task CloseFailedTransferAsync(Task reply, Stream local, Stream remote)
        {
            try { await reply.ConfigureAwait(false); }
            catch (Exception)
            {
                local.Dispose();
                remote.Dispose();
            }
        }

        private async Task<T> RequestAsync<T>(
            Func<CancellationToken, Task<T>> invoke,
            TimeSpan budget,
            CancellationToken cancellationToken,
            bool artwork = false,
            bool duringStartup = false,
            bool waitForCapacity = false)
        {
            var operation = this.SendRequestAsync(invoke, budget, artwork, duringStartup, waitForCapacity,
                cancellationToken);
            try { return await operation.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _ = ObserveCanceledRequestAsync(operation);
                throw;
            }
        }

        private static async Task ObserveCanceledRequestAsync(Task operation) =>
            await operation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        private async Task<T> SendRequestAsync<T>(
            Func<CancellationToken, Task<T>> invoke,
            TimeSpan budget,
            bool artwork,
            bool duringStartup,
            bool waitForCapacity,
            CancellationToken caller)
        {
            while (true)
            {
                Task capacity;
                lock (this._requestGate)
                {
                    if (this._closing) { throw new IOException("The worker connection has closed."); }

                    if (this._active < 64)
                    {
                        this._active++;
                        break;
                    }

                    if (!waitForCapacity) { throw new IOException("The worker request queue is full."); }

                    capacity = (this._capacityAvailable
                        ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                }

                await capacity.WaitAsync(caller).ConfigureAwait(false);
            }

            using var completion = new CancellationTokenSource();
            var deadlineGate = new Lock();
            using var canceledWait = caller.Register(() =>
            {
                lock (deadlineGate) { completion.CancelAfter(Timeout.InfiniteTimeSpan); }
            });
            using var invocation
                = CancellationTokenSource.CreateLinkedTokenSource(caller, this._closed.Token,
                    duringStartup ? startup : default);
            using var admission = CancellationTokenSource.CreateLinkedTokenSource(invocation.Token);
            if (!duringStartup) { admission.CancelAfter(budget); }

            using var expired = completion.Token.Register(() =>
                this.Close("The worker request timed out; its outcome may be unknown."));

            var admitted = false;
            var artworkAdmitted = false;
            var sent = false;

            try
            {
                if (artwork)
                {
                    await this._artworkAdmission.WaitAsync(admission.Token).ConfigureAwait(false);
                    artworkAdmitted = true;
                }

                await this._admission.WaitAsync(admission.Token).ConfigureAwait(false);
                admitted = true;
                var previous = WorkerMessageHandler.CurrentSend.Value;
                Task<T> request;
                try
                {
                    WorkerMessageHandler.CurrentSend.Value = new WorkerMessageHandler.SendScope(() =>
                    {
                        lock (deadlineGate)
                        {
                            sent = true;
                            if (!duringStartup && !caller.IsCancellationRequested) { completion.CancelAfter(budget); }
                        }
                    }, admission.Token);
                    request = invoke(invocation.Token);
                }
                finally { WorkerMessageHandler.CurrentSend.Value = previous; }

                return await request.ConfigureAwait(false);
            }
            catch (Exception ex) when (completion.IsCancellationRequested)
            {
                throw new TimeoutException("The worker request timed out; its outcome may be unknown.", ex);
            }
            catch (Exception ex) when (this.Protocol.IsClosed)
            {
                this.Close("The worker connection closed during the request.");
                throw new IOException("The worker connection closed during the request.", ex);
            }
            catch (OperationCanceledException) when (!caller.IsCancellationRequested &&
                                                     !this._closed.IsCancellationRequested && !sent)
            {
                throw new TimeoutException("The worker request timed out before it was sent.");
            }
            catch (Exception ex) when (ex is RemoteInvocationException or ConnectionLostException
                                           or ObjectDisposedException)
            {
                throw new IOException(ex.Message, ex);
            }
            finally
            {
                if (admitted) { this._admission.Release(); }

                if (artworkAdmitted) { this._artworkAdmission.Release(); }

                lock (this._requestGate)
                {
                    if (--this._active == 0 && this._closing) { this._drained.TrySetResult(); }

                    this._capacityAvailable?.TrySetResult();
                    this._capacityAvailable = null;
                }
            }
        }

        private void AcceptNotice(Guid? epoch, Action accept)
        {
            try
            {
                if (epoch is null || epoch == Guid.Empty || epoch != this.Epoch)
                {
                    throw new InvalidDataException("The notice is missing or belongs to a different worker lifetime.");
                }

                accept();
            }
            catch (Exception ex)
            {
                this.Close(ex.Message);
            }
        }

        public void Invalidate(ImmutableArray<MediaBackendObservationRequest> requests)
        {
            lock (this._requestGate)
            {
                if (this._closing)
                {
                    return;
                }

                foreach (var request in requests)
                {
                    this._invalidations[request.SessionId]
                        = this._invalidations.GetValueOrDefault(request.SessionId) | request.Changes;
                }

                if (!this._invalidating)
                {
                    this._invalidating = true;
                    _ = Task.Run(this.SendInvalidationsAsync, CancellationToken.None);
                }
            }
        }

        private async Task SendInvalidationsAsync()
        {
            while (true)
            {
                ImmutableArray<MediaBackendObservationRequest> requests;
                lock (this._requestGate)
                {
                    if (this._closing || this._invalidations.Count == 0)
                    {
                        this._invalidating = false;
                        return;
                    }

                    requests =
                    [
                        .. this._invalidations.Select(static entry =>
                            new MediaBackendObservationRequest(entry.Key, entry.Value))
                    ];
                    this._invalidations.Clear();
                }

                if (!await OutOfProcessMediaBackend.SendInvalidationsAsync(this, requests).ConfigureAwait(false))
                {
                    lock (this._requestGate)
                    {
                        foreach (var request in requests)
                        {
                            this._invalidations[request.SessionId]
                                = this._invalidations.GetValueOrDefault(request.SessionId) | request.Changes;
                        }
                    }
                }
            }
        }

        private void RecordFailure(string? reason)
        {
            if (!string.IsNullOrEmpty(reason)) { Interlocked.CompareExchange(ref this._failure, reason, null); }
        }

        public void Close(string? reason = null)
        {
            this.RecordFailure(this.Protocol.Handler.Failure?.Message ?? reason);
            this.Health.Complete();

            // Publish closure before cancellation or RPC disposal can complete requests.
            lock (this._requestGate)
            {
                if (!this._closing)
                {
                    this._closing = true;
                    this._capacityAvailable?.TrySetResult();
                    this._capacityAvailable = null;
                    _ = this._closed.CancelAsync();
                    if (this._active == 0) { this._drained.TrySetResult(); }
                }
            }

            this.Protocol.Dispose();
        }

        private sealed class ActivationCommand(MediaBackendCommand command, CancellationToken cancellation)
        {
            public int CallbackUsed;
            public MediaBackendCommand Command { get; } = command;
            public CancellationToken Cancellation { get; } = cancellation;
        }
    }
}