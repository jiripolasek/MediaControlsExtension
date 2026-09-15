using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Pipes;
using System.Threading.Channels;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting.Tests;

internal static class ReviewTimeoutTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("Canceling artwork during a partial frame preserves the connection", ArtworkFrameCancellationAsync),
        ("Canceling a hosted activation during its partial frame preserves the connection", ActivationFrameCancellationAsync),
        ("A slow artwork read does not block a ready peer", ConcurrentArtworkAsync),
        ("Snapshot and queued policy work use their separate deadlines", ObservationAndPolicyAsync),
        ("Noncooperative snapshots still close within the observation budget", ObservationDeadlineAsync),
        ("An unsent admission timeout preserves pending peers and the connection", AdmissionDeadlineAsync),
        ("Snapshot recovery reports the last read error before disconnecting", RecoveryFaultAsync),
        ("A blocked snapshot fault report cannot delay worker shutdown", BlockedRecoveryFaultAsync),
    ];

    private static Task RecoveryFaultAsync() => WithHostAsync(new ControlledBackend(), async host =>
    {
        var reads = 0;
        host.Backend.SnapshotRead = _ => Task.FromException(new IOException(
            Interlocked.Increment(ref reads) == 1 ? "First read failure" : "Last read failure"));
        host.Backend.InvalidateObservations([]);
        var fault = await host.ReadUntilAsync(static message => message.Kind == MessageKind.Fault).ConfigureAwait(false);
        HostingTests.Check(fault.Error?.Contains("Last read failure", StringComparison.Ordinal) == true && reads == 2,
            "Recovery expiry did not report the latest backend error.");
        await host.Worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }, observationTimeout: TimeSpan.FromMilliseconds(850));

    private static Task BlockedRecoveryFaultAsync() => WithHostAsync(new ControlledBackend(), async host =>
    {
        host.Backend.SnapshotRead = _ => Task.FromException(new IOException(new string('x', 2 * 1024 * 1024)));
        host.Backend.InvalidateObservations([]);
        await host.ReadUntilAsync(static message => message.Kind == MessageKind.SnapshotFailure).ConfigureAwait(false);
        await host.Worker.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
    }, observationTimeout: TimeSpan.FromMilliseconds(400));

    private static Task ArtworkFrameCancellationAsync() => WithHostAsync(new ControlledBackend(), async host =>
    {
        CancellationToken artworkCancellation = default;
        host.Backend.ArtworkRead = (key, token) =>
        {
            artworkCancellation = token;
            return ValueTask.FromResult<MediaArtworkContent?>(new("image/png", new byte[256 * 1024], null));
        };
        await host.SendAsync(MessageKind.Artwork, 1, new(new(1), 1)).ConfigureAwait(false);
        await host.ReadUntilAsync(static message => message.Kind == MessageKind.ArtworkStart).ConfigureAwait(false);
        var header = new byte[4];
        await host.Pipe.ReadExactlyAsync(header, host.Cancellation).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        HostingTests.Check(length < -44, "The worker did not start a binary artwork chunk.");
        await host.Pipe.ReadExactlyAsync(new byte[1], host.Cancellation).ConfigureAwait(false);

        await host.SendAsync(MessageKind.Cancel, 1).ConfigureAwait(false);
        await HostingTests.EventuallyAsync(() => artworkCancellation.IsCancellationRequested).ConfigureAwait(false);
        await host.Pipe.ReadExactlyAsync(new byte[-length - 1], host.Cancellation).ConfigureAwait(false);
        var canceled = await host.ReadUntilAsync(static message => message.Kind == MessageKind.Reply && message.RequestId == 1).ConfigureAwait(false);
        HostingTests.Check(canceled.Error is not null, "Canceled artwork was not acknowledged.");
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
                return new("image/png", new byte[128], null);
            },
        };
        try
        {
            await WithHostAsync(backend, async host =>
            {
                await host.SendAsync(MessageKind.Artwork, 1, new(new(1), 1)).ConfigureAwait(false);
                await entered.Task.WaitAsync(host.Cancellation).ConfigureAwait(false);
                await host.SendAsync(MessageKind.Artwork, 2, new(new(1), 2)).ConfigureAwait(false);
                await host.ReadUntilAsync(static message => message.Kind == MessageKind.ArtworkEnd && message.RequestId == 2).ConfigureAwait(false);
                HostingTests.Check(!release.Task.IsCompleted, "The ready artwork waited for the slow backend read.");
                release.TrySetResult();
                await host.ReadUntilAsync(static message => message.Kind == MessageKind.ArtworkEnd && message.RequestId == 1).ConfigureAwait(false);
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
                await host.Protocol.WriteAsync(new(MessageKind.Policy, host.Owner, host.Epoch, 1)
                {
                    Policy = new(1, []),
                }, host.Cancellation).ConfigureAwait(false);
                await Task.Delay(750, host.Cancellation).ConfigureAwait(false);
                HostingTests.Check(!policyEntered.Task.IsCompleted, "Policy bypassed an in-flight snapshot.");
                readRelease.TrySetResult();
                await policyEntered.Task.WaitAsync(host.Cancellation).ConfigureAwait(false);
                await Task.Delay(750, host.Cancellation).ConfigureAwait(false);
                policyRelease.TrySetResult();
                var reply = await host.ReadUntilAsync(static message => message.Kind == MessageKind.Reply && message.RequestId == 1).ConfigureAwait(false);
                HostingTests.Check(reply.Error is null && reply.Policy?.Revision == 1, "Policy exceeded the command budget but should have succeeded.");
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
        await host.Protocol.WriteAsync(new(MessageKind.Execute, host.Owner, host.Epoch, 1)
        {
            Command = new(new(1), 1, MediaOperation.ActivateSource, []),
        }, host.Cancellation).ConfigureAwait(false);
        var header = new byte[4];
        await host.Pipe.ReadExactlyAsync(header, host.Cancellation).ConfigureAwait(false);
        var payload = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
        await host.Pipe.ReadExactlyAsync(payload.AsMemory(0, 1), host.Cancellation).ConfigureAwait(false);
        await host.Protocol.WriteAsync(new(MessageKind.Cancel, host.Owner, host.Epoch, 1), host.Cancellation).ConfigureAwait(false);
        await HostingTests.EventuallyAsync(() => host.Backend.ActivationCancellation.IsCancellationRequested).ConfigureAwait(false);
        await host.Pipe.ReadExactlyAsync(payload.AsMemory(1), host.Cancellation).ConfigureAwait(false);
        var activation = System.Text.Json.JsonSerializer.Deserialize(payload, WireJsonContext.Default.WireMessage);
        HostingTests.Check(activation?.Kind == MessageKind.ActivateSource && activation.Activation?.MediaTitle?.Length == 2 * 1024 * 1024,
            "Canceling the command truncated its activation frame.");
        await host.ReadUntilAsync(static message => message.Kind == MessageKind.Reply && message.RequestId == 1).ConfigureAwait(false);
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
                backend.SnapshotRead = _ => { entered.TrySetResult(); return release.Task; };
                backend.InvalidateObservations([]);
                await entered.Task.WaitAsync(host.Cancellation).ConfigureAwait(false);
                await host.Worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                HostingTests.Check(!release.Task.IsCompleted, "The test released the hung snapshot prematurely.");
                HostingTests.Check(await host.Worker.ConfigureAwait(false) == 2, "A hung snapshot was reported as graceful cleanup.");
            }, observationTimeout: TimeSpan.FromMilliseconds(150)).ConfigureAwait(false);
        }
        finally { release.TrySetResult(); }
    }

    private static async Task AdmissionDeadlineAsync()
    {
        await using var backend = new OutOfProcessMediaBackend(Program.Options("review-admission") with
        {
            RequestTimeout = TimeSpan.FromSeconds(2), MaximumRestarts = 0,
        });
        await backend.StartAsync(default).ConfigureAwait(false);
        var snapshot = await HostingTests.WaitForSnapshotAsync(backend).ConfigureAwait(false);
        var epoch = backend.WorkerEpoch;
        using var cancel = new CancellationTokenSource();
        var requests = Enumerable.Range(0, 32).Select(_ => backend.ExecuteAsync(HostingTests.Command(snapshot, MediaOperation.Stop), cancel.Token)).ToArray();
        await HostingTests.EventuallyAsync(async () =>
            (await backend.ReadSnapshotAsync(default).ConfigureAwait(false)).Sessions[0].MediaProperties.Title == "Admitted 32").ConfigureAwait(false);
        cancel.Cancel();
        try { await Task.WhenAll(requests).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        var unsent = await backend.ExecuteAsync(HostingTests.Command(snapshot, MediaOperation.Play), default).ConfigureAwait(false);
        HostingTests.Check(unsent.Status == MediaBackendCommandStatus.Unavailable && unsent.DiagnosticMessage?.Contains("before it was sent", StringComparison.Ordinal) == true,
            $"The admission timeout did not report an unsent request: {unsent.Status}, {unsent.DiagnosticMessage}.");
        await HostingTests.EventuallyAsync(async () =>
            (await backend.ReadSnapshotAsync(default).ConfigureAwait(false)).Sessions[0].MediaProperties.Title == "Released").ConfigureAwait(false);
        var result = await backend.ExecuteAsync(HostingTests.Command(snapshot, MediaOperation.Play), default).ConfigureAwait(false);
        HostingTests.Check(result.Status == MediaBackendCommandStatus.Completed && backend.WorkerEpoch == epoch && backend.RestartCount == 0,
            "An unsent request disconnected its already admitted peers.");
    }

    private static async Task WithHostAsync(ControlledBackend backend, Func<HostConnection, Task> action,
        TimeSpan? requestTimeout = null, TimeSpan? observationTimeout = null, TimeSpan? policyTimeout = null)
    {
        var owner = Guid.NewGuid();
        var name = $"LOCAL\\MediaHostingTests.{owner:N}";
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var worker = MediaBackendHost.RunAsync(name, owner, Environment.ProcessId, "controlled",
            context => { backend.Context = context; return backend; }, TimeSpan.FromMilliseconds(300));
        await pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
        using var protocol = new PipeProtocol(pipe);
        await protocol.WriteAsync(new(MessageKind.Hello, owner)
        {
            BackendId = "controlled", Policy = new(0, []), CanActivateSource = true,
            RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(5),
            ObservationTimeout = observationTimeout ?? TimeSpan.FromSeconds(10),
            PolicyTimeout = policyTimeout ?? TimeSpan.FromSeconds(10),
        }, deadline.Token).ConfigureAwait(false);
        var welcome = await protocol.ReadAsync(deadline.Token).ConfigureAwait(false);
        var host = new HostConnection(backend, pipe, protocol, owner, welcome.Epoch, worker, deadline.Token);
        try
        {
            await host.ReadUntilAsync(static message => message.Kind == MessageKind.Snapshot).ConfigureAwait(false);
            await action(host).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        finally
        {
            protocol.Dispose();
            await worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await backend.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record HostConnection(ControlledBackend Backend, NamedPipeServerStream Pipe, PipeProtocol Protocol,
        Guid Owner, Guid Epoch, Task<int> Worker, CancellationToken Cancellation)
    {
        public Task SendAsync(MessageKind kind, long id, MediaArtworkKey? key = null) =>
            this.Protocol.WriteAsync(new(kind, this.Owner, this.Epoch, id) { ArtworkKey = key }, this.Cancellation);

        public async Task<WireMessage> ReadUntilAsync(Func<WireMessage, bool> predicate)
        {
            while (true)
            {
                var message = await this.Protocol.ReadAsync(this.Cancellation).ConfigureAwait(false);
                if (predicate(message)) { return message; }
                HostingTests.Check(message.Kind != MessageKind.Fault, message.Error ?? "Worker faulted.");
            }
        }

        public async Task CheckCommandAsync(long id)
        {
            await this.Protocol.WriteAsync(new(MessageKind.Execute, this.Owner, this.Epoch, id)
            {
                Command = new(new(1), 1, MediaOperation.Play, []),
            }, this.Cancellation).ConfigureAwait(false);
            var reply = await this.ReadUntilAsync(message => message.Kind == MessageKind.Reply && message.RequestId == id).ConfigureAwait(false);
            HostingTests.Check(reply.Result?.Status == MediaBackendCommandStatus.Completed, "The connection did not remain usable.");
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
        public IAsyncEnumerable<MediaBackendSignal> WatchAsync(CancellationToken cancellationToken) => this._signals.Reader.ReadAllAsync(cancellationToken);
        public void InvalidateObservations(ImmutableArray<MediaBackendObservationRequest> requests) => this._signals.Writer.TryWrite(MediaBackendSignal.ObservationsChanged);
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
        public ValueTask<MediaArtworkContent?> GetArtworkAsync(MediaArtworkKey key, CancellationToken cancellationToken) => this.ArtworkRead!(key, cancellationToken);
        public async Task<MediaBackendCommandResult> ExecuteAsync(MediaBackendCommand command, CancellationToken cancellationToken)
        {
            if (command.Operation == MediaOperation.ActivateSource)
            {
                this.ActivationCancellation = cancellationToken;
                var activated = await this.Context!.TryActivateSourceAsync("spike.player", new string('x', 2 * 1024 * 1024), cancellationToken).ConfigureAwait(false);
                return new(activated ? MediaBackendCommandStatus.Completed : MediaBackendCommandStatus.Failed, null);
            }
            return await this._inner.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        }
        public ValueTask DisposeAsync() => this._inner.DisposeAsync();
    }
}