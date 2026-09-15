using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Pipes;
using System.Threading.Channels;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting.Tests;

internal static class ReviewTimeoutTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("Canceling artwork during a partial frame preserves the connection", ArtworkFrameCancellationAsync),
        ("Canceling a hosted activation during its partial frame preserves the connection",
            ActivationFrameCancellationAsync),
        ("A slow artwork read does not block a ready peer", ConcurrentArtworkAsync),
        ("Snapshot and queued policy work use their separate deadlines", ObservationAndPolicyAsync),
        ("Noncooperative snapshots still close within the observation budget", ObservationDeadlineAsync),
        ("Blocked or failing watchdog diagnostics cannot delay disconnect", WatchdogDiagnosticsAsync),
        ("An unsent admission timeout preserves pending peers and the connection", AdmissionDeadlineAsync),
        ("Snapshot recovery reports the last read error before disconnecting", RecoveryFaultAsync),
        ("A blocked snapshot fault report cannot delay worker shutdown", BlockedRecoveryFaultAsync)
    ];

    private static Task RecoveryFaultAsync() => WithHostAsync(new ControlledBackend(), async host =>
    {
        var reads = 0;
        host.Backend.SnapshotRead = _ => Task.FromException(new IOException(
            Interlocked.Increment(ref reads) == 1 ? "First read failure" : "Last read failure"));
        host.Backend.InvalidateObservations([]);
        var fault = await host.ReadUntilAsync<WorkerFault>().ConfigureAwait(false);
        HostingTests.Check(fault.Error?.Contains("Last read failure", StringComparison.Ordinal) == true && reads == 2,
            "Recovery expiry did not report the latest backend error.");
        await host.Worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }, observationTimeout: TimeSpan.FromMilliseconds(850));

    private static Task BlockedRecoveryFaultAsync() => WithHostAsync(new ControlledBackend(), async host =>
    {
        host.Backend.SnapshotRead = _ => Task.FromException(new IOException(new string('x', 2 * 1024 * 1024)));
        var largeFrames = 0;
        host.OnLargeFrame = () =>
            Interlocked.Increment(ref largeFrames) == 1
                ? Task.CompletedTask
                : Task.Delay(Timeout.InfiniteTimeSpan, host.Cancellation);
        host.Backend.InvalidateObservations([]);
        await host.ReadUntilAsync<WorkerSnapshotFailure>().ConfigureAwait(false);
        await host.Worker.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
    }, observationTimeout: TimeSpan.FromMilliseconds(400));

    private static Task ArtworkFrameCancellationAsync() => WithHostAsync(new ControlledBackend(), async host =>
    {
        CancellationToken artworkCancellation = default;
        host.Backend.ArtworkRead = (key, token) =>
        {
            artworkCancellation = token;
            return ValueTask.FromResult<MediaArtworkContent?>(new MediaArtworkContent("image/png",
                new byte[PipeProtocol.MaximumArtworkBytes], null));
        };
        var (local, remote) = FullDuplexStream.CreatePair();
        using (local)
        using (remote)
        using (var cancel = new CancellationTokenSource())
        {
            var copy = host.Rpc.CopyArtworkAsync(host.Epoch, new MediaArtworkKey(new MediaSessionId(1), 1), remote,
                cancel.Token);
            await local.ReadExactlyAsync(new byte[5], host.Cancellation).ConfigureAwait(false);
            await host.CheckCommandAsync(2).ConfigureAwait(false);
            HostingTests.Check(!copy.IsCompleted, "The test did not backpressure the artwork stream.");
            cancel.Cancel();
            await HostingTests.EventuallyAsync(() => artworkCancellation.IsCancellationRequested).ConfigureAwait(false);
            local.Dispose();
            remote.Dispose();
            try { await copy.WaitAsync(host.Cancellation).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or RemoteInvocationException) { }
        }

        await host.CheckCommandAsync(2).ConfigureAwait(false);
    });

    private static async Task ConcurrentArtworkAsync()
    {
        var entered = NewCompletion();
        var release = NewCompletion();
        var backend = new ControlledBackend
        {
            ArtworkRead = async (key, token) =>
            {
                if (key.Version == 1)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token).ConfigureAwait(false);
                }

                return new MediaArtworkContent("image/png", new byte[128], null);
            }
        };
        try
        {
            await WithHostAsync(backend, async host =>
            {
                var slow = host.ArtworkAsync(new MediaArtworkKey(new MediaSessionId(1), 1));
                await entered.Task.WaitAsync(host.Cancellation).ConfigureAwait(false);
                await host.ArtworkAsync(new MediaArtworkKey(new MediaSessionId(1), 2)).ConfigureAwait(false);
                HostingTests.Check(!release.Task.IsCompleted, "The ready artwork waited for the slow backend read.");
                release.TrySetResult();
                await slow.ConfigureAwait(false);
                await host.CheckCommandAsync(3).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        finally { release.TrySetResult(); }
    }

    private static async Task ObservationAndPolicyAsync()
    {
        var readEntered = NewCompletion();
        var readRelease = NewCompletion();
        var policyEntered = NewCompletion();
        var policyRelease = NewCompletion();
        var backend = new ControlledBackend();
        try
        {
            await WithHostAsync(backend, async host =>
            {
                backend.SnapshotRead = async token =>
                {
                    readEntered.TrySetResult();
                    await readRelease.Task.WaitAsync(token).ConfigureAwait(false);
                };
                backend.PolicyApply = async (_, token) =>
                {
                    policyEntered.TrySetResult();
                    await policyRelease.Task.WaitAsync(token).ConfigureAwait(false);
                };
                backend.InvalidateObservations([]);
                await readEntered.Task.WaitAsync(host.Cancellation).ConfigureAwait(false);
                var policy = host.Rpc.ApplyPolicyAsync(host.Epoch, new SourcePolicyMessage(1, []), host.Cancellation);
                await Task.Delay(750, host.Cancellation).ConfigureAwait(false);
                HostingTests.Check(!policyEntered.Task.IsCompleted, "Policy bypassed an in-flight snapshot.");
                readRelease.TrySetResult();
                await policyEntered.Task.WaitAsync(host.Cancellation).ConfigureAwait(false);
                await Task.Delay(750, host.Cancellation).ConfigureAwait(false);
                policyRelease.TrySetResult();
                HostingTests.Check(await policy.ConfigureAwait(false) == 1,
                    "Policy exceeded the command budget but should have succeeded.");
                await host.CheckCommandAsync(2).ConfigureAwait(false);
            }, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        }
        finally
        {
            readRelease.TrySetResult();
            policyRelease.TrySetResult();
        }
    }

    private static Task ActivationFrameCancellationAsync() => WithHostAsync(new ControlledBackend(), async host =>
    {
        var entered = NewCompletion();
        var release = NewCompletion();
        host.OnLargeFrame = () =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        using var cancel = new CancellationTokenSource();
        var execute = host.Rpc.ExecuteAsync(
            new WorkerCommand(host.Epoch, 1,
                new MediaBackendCommand(new MediaBackendSessionId(1), 1, MediaOperation.ActivateSource, [])),
            cancel.Token);
        try
        {
            await entered.Task.WaitAsync(host.Cancellation).ConfigureAwait(false);
            cancel.Cancel();
            await HostingTests.EventuallyAsync(() => host.Backend.ActivationCancellation.IsCancellationRequested)
                .ConfigureAwait(false);
        }
        finally { release.TrySetResult(); }

        var activation = await host.Activation.Task.WaitAsync(host.Cancellation).ConfigureAwait(false);
        HostingTests.Check(activation.Activation.MediaTitle.Length == 2 * 1024 * 1024,
            "Canceling the command truncated its activation frame.");
        try { await execute.ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        await host.CheckCommandAsync(2).ConfigureAwait(false);
    });

    private static async Task ObservationDeadlineAsync()
    {
        var entered = NewCompletion();
        var release = NewCompletion();
        var backend = new ControlledBackend();
        try
        {
            await WithHostAsync(backend, async host =>
            {
                backend.SnapshotRead = _ =>
                {
                    entered.TrySetResult();
                    return release.Task;
                };
                backend.InvalidateObservations([]);
                await entered.Task.WaitAsync(host.Cancellation).ConfigureAwait(false);
                await host.Worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                HostingTests.Check(!release.Task.IsCompleted, "The test released the hung snapshot prematurely.");
                HostingTests.Check(await host.Worker.ConfigureAwait(false) == 2,
                    "A hung snapshot was reported as graceful cleanup.");
            }, observationTimeout: TimeSpan.FromMilliseconds(150)).ConfigureAwait(false);
        }
        finally { release.TrySetResult(); }
    }

    private static async Task WatchdogDiagnosticsAsync()
    {
        var readEntered = NewCompletion();
        var readRelease = NewCompletion();
        var reporting = NewCompletion();
        using var reportRelease = new ManualResetEventSlim();
        var backend = new ControlledBackend();
        try
        {
            await WithHostAsync(backend, async host =>
            {
                backend.SnapshotRead = _ =>
                {
                    readEntered.TrySetResult();
                    return readRelease.Task;
                };
                backend.InvalidateObservations([]);
                await readEntered.Task.WaitAsync(host.Cancellation).ConfigureAwait(false);
                await reporting.Task.WaitAsync(host.Cancellation).ConfigureAwait(false);
                await host.Disconnected.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                HostingTests.Check(!reportRelease.IsSet, "The test released the diagnostic sink before disconnecting.");
                reportRelease.Set();
                await host.Worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }, observationTimeout: TimeSpan.FromMilliseconds(150), reportFailure: _ =>
            {
                reporting.TrySetResult();
                reportRelease.Wait(TimeSpan.FromSeconds(5));
                throw new IOException("Injected diagnostic failure.");
            }).ConfigureAwait(false);
        }
        finally
        {
            reportRelease.Set();
            readRelease.TrySetResult();
        }
    }

    private static async Task AdmissionDeadlineAsync()
    {
        await using var backend = new OutOfProcessMediaBackend(Program.Options("review-admission") with
        {
            RequestTimeout = TimeSpan.FromSeconds(2), MaximumRestarts = 0
        });
        await backend.StartAsync(default).ConfigureAwait(false);
        var snapshot = await HostingTests.WaitForSnapshotAsync(backend).ConfigureAwait(false);
        var epoch = backend.WorkerEpoch;
        using var cancel = new CancellationTokenSource();
        var requests = Enumerable.Range(0, 32).Select(_ =>
            backend.ExecuteAsync(HostingTests.Command(snapshot, MediaOperation.Stop), cancel.Token)).ToArray();
        await HostingTests.EventuallyAsync(async () =>
            (await backend.ReadSnapshotAsync(default).ConfigureAwait(false)).Sessions[0].MediaProperties.Title ==
            "Admitted 32").ConfigureAwait(false);
        cancel.Cancel();
        try { await Task.WhenAll(requests).ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        var unsent = await backend.ExecuteAsync(HostingTests.Command(snapshot, MediaOperation.Play), default)
            .ConfigureAwait(false);
        HostingTests.Check(
            unsent.Status == MediaBackendCommandStatus.Unavailable &&
            unsent.DiagnosticMessage?.Contains("before it was sent", StringComparison.Ordinal) == true,
            $"The admission timeout did not report an unsent request: {unsent.Status}, {unsent.DiagnosticMessage}.");
        await HostingTests.EventuallyAsync(async () =>
            (await backend.ReadSnapshotAsync(default).ConfigureAwait(false)).Sessions[0].MediaProperties.Title ==
            "Released").ConfigureAwait(false);
        var result = await backend.ExecuteAsync(HostingTests.Command(snapshot, MediaOperation.Play), default)
            .ConfigureAwait(false);
        HostingTests.Check(
            result.Status == MediaBackendCommandStatus.Completed && backend.WorkerEpoch == epoch &&
            backend.RestartCount == 0,
            "An unsent request disconnected its already admitted peers.");
    }

    private static async Task WithHostAsync(
        ControlledBackend backend,
        Func<HostConnection, Task> action,
        TimeSpan? requestTimeout = null,
        TimeSpan? observationTimeout = null,
        TimeSpan? policyTimeout = null,
        Action<string>? reportFailure = null)
    {
        var owner = Guid.NewGuid();
        var name = $"LOCAL\\MediaHostingTests.{owner:N}";
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var worker = MediaBackendHost.RunAsync(name, owner, Environment.ProcessId, "controlled",
            context =>
            {
                backend.Context = context;
                return backend;
            }, TimeSpan.FromMilliseconds(300), reportFailure);
        await pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
        await using var multiplexing = await MultiplexingStream.CreateAsync(pipe,
            new MultiplexingStream.Options
            {
                ProtocolMajorVersion = 2, DefaultChannelReceivingWindowSize = PipeProtocol.MaximumChunkBytes
            }, deadline.Token).ConfigureAwait(false);
        var channel = await multiplexing.OfferChannelAsync("rpc", cancellationToken: deadline.Token)
            .ConfigureAwait(false);
        HostConnection host = null!;
        using var stream
            = new ReviewPartialRequestPeer.PartialReadStream(channel.AsStream(), () => host.OnLargeFrame());
        await using var endpoint = new WorkerRpcEndpoint(multiplexing,
            new WorkerMessageHandler(stream, WorkerRpcEndpoint.CreateFormatter(multiplexing)));
        var rpc = endpoint.Rpc.Attach<IWorkerRpc>();
        host = new HostConnection(backend, rpc, worker, deadline.Token) { Disconnected = endpoint.Rpc.Completion };
        endpoint.Rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IOwnerNotifications>(), host, null);
        endpoint.Rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IOwnerRpc>(), host, null);
        endpoint.Rpc.StartListening();
        var welcome = await rpc.InitializeAsync(new(PipeProtocol.Version, owner, "controlled", new(0, []), true, null,
            requestTimeout ?? TimeSpan.FromSeconds(5), observationTimeout ?? TimeSpan.FromSeconds(10),
            policyTimeout ?? TimeSpan.FromSeconds(10)), deadline.Token).ConfigureAwait(false);
        host.Epoch = welcome.Epoch;
        await endpoint.Rpc.NotifyAsync(nameof(IWorkerNotifications.Begin), welcome.Epoch).ConfigureAwait(false);
        try
        {
            await host.ReadUntilAsync<WorkerSnapshot>().ConfigureAwait(false);
            await action(host).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        finally
        {
            deadline.Cancel();
            endpoint.Dispose();
            await worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await backend.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record HostConnection(
        ControlledBackend Backend,
        IWorkerRpc Rpc,
        Task<int> Worker,
        CancellationToken Cancellation)
        : IOwnerRpc, IOwnerNotifications
    {
        private readonly Channel<object> _notices = Channel.CreateUnbounded<object>();
        public Guid Epoch { get; set; }
        public Task Disconnected { get; init; } = Task.CompletedTask;
        public Func<Task> OnLargeFrame { get; set; } = static () => Task.CompletedTask;

        public TaskCompletionSource<WorkerActivation> Activation { get; }
            = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Snapshot(WorkerSnapshot notice) => this._notices.Writer.TryWrite(notice);
        public void SnapshotFailed(WorkerSnapshotFailure failure) => this._notices.Writer.TryWrite(failure);

        public Task ReportFaultAsync(WorkerFault fault, CancellationToken cancellationToken)
        {
            this._notices.Writer.TryWrite(fault);
            return Task.CompletedTask;
        }

        public Task<bool> ActivateSourceAsync(WorkerActivation request, CancellationToken cancellationToken)
        {
            this.Activation.TrySetResult(request);
            return Task.FromResult(false);
        }

        public async Task<T> ReadUntilAsync<T>()
        {
            while (true)
            {
                var message = await this._notices.Reader.ReadAsync(this.Cancellation).ConfigureAwait(false);
                if (message is T expected) { return expected; }

                if (message is WorkerFault fault) { throw new IOException(fault.Error); }
            }
        }

        public async Task CheckCommandAsync(long id)
        {
            var result = await this.Rpc
                .ExecuteAsync(
                    new WorkerCommand(this.Epoch, id,
                        new MediaBackendCommand(new MediaBackendSessionId(1), 1, MediaOperation.Play, [])),
                    this.Cancellation).ConfigureAwait(false);
            HostingTests.Check(result.Status == MediaBackendCommandStatus.Completed,
                "The connection did not remain usable.");
        }

        public async Task ArtworkAsync(MediaArtworkKey key)
        {
            var (local, remote) = FullDuplexStream.CreatePair();
            using (local)
            using (remote)
            {
                var copy = this.Rpc.CopyArtworkAsync(this.Epoch, key, remote, this.Cancellation);
                var header = new byte[4];
                await local.ReadExactlyAsync(header, this.Cancellation).ConfigureAwait(false);
                var bytes = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
                await local.ReadExactlyAsync(bytes, this.Cancellation).ConfigureAwait(false);
                var result = await copy.ConfigureAwait(false);
                HostingTests.Check(result?.Length == bytes.Length, "Artwork was truncated.");
            }
        }
    }

    private sealed class ControlledBackend : IMediaSourcePolicyBackend
    {
        private readonly SyntheticBackend _inner = new();
        private readonly Channel<MediaBackendSignal> _signals = Channel.CreateUnbounded<MediaBackendSignal>();
        public Func<CancellationToken, Task>? SnapshotRead { get; set; }
        public HostedBackendContext? Context { get; set; }
        public CancellationToken ActivationCancellation { get; private set; }
        public Func<MediaBackendSourcePolicy, CancellationToken, Task>? PolicyApply { get; set; }
        public Func<MediaArtworkKey, CancellationToken, ValueTask<MediaArtworkContent?>>? ArtworkRead { get; set; }
        public Task StartAsync(CancellationToken cancellationToken) => this._inner.StartAsync(cancellationToken);

        public IAsyncEnumerable<MediaBackendSignal> WatchAsync(CancellationToken cancellationToken) =>
            this._signals.Reader.ReadAllAsync(cancellationToken);

        public void InvalidateObservations(ImmutableArray<MediaBackendObservationRequest> requests) =>
            this._signals.Writer.TryWrite(MediaBackendSignal.ObservationsChanged);

        public async Task<MediaBackendSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
        {
            if (this.SnapshotRead is { } read) { await read(cancellationToken).ConfigureAwait(false); }

            return await this._inner.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task ApplySourcePolicyAsync(MediaBackendSourcePolicy policy, CancellationToken cancellationToken)
        {
            if (this.PolicyApply is { } apply) { await apply(policy, cancellationToken).ConfigureAwait(false); }

            await this._inner.ApplySourcePolicyAsync(policy, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<MediaArtworkContent?>
            GetArtworkAsync(MediaArtworkKey key, CancellationToken cancellationToken) =>
            this.ArtworkRead!(key, cancellationToken);

        public async Task<MediaBackendCommandResult> ExecuteAsync(
            MediaBackendCommand command,
            CancellationToken cancellationToken)
        {
            if (command.Operation == MediaOperation.ActivateSource)
            {
                this.ActivationCancellation = cancellationToken;
                var activated = await this.Context!
                    .TryActivateSourceAsync("spike.player", new string('x', 2 * 1024 * 1024), cancellationToken)
                    .ConfigureAwait(false);
                return new MediaBackendCommandResult(
                    activated ? MediaBackendCommandStatus.Completed : MediaBackendCommandStatus.Failed, null);
            }

            return await this._inner.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => this._inner.DisposeAsync();
    }
}