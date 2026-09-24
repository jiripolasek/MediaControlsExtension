using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting.Tests;

internal static class ProtocolTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("Frame parser rejects invalid lengths", InvalidLengthsAsync),
        ("Frame parser rejects a truncated payload", TruncatedFrameAsync),
        ("Frame parser rejects malformed JSON", InvalidJsonAsync),
        ("RPC releases snapshot buffers before reading the next message", BufferCompletionAsync),
        ("Worker releases rejected method buffers", () => RejectedBuffersAsync("Unknown", 0)),
        ("Worker releases rejected control request buffers",
            () => RejectedBuffersAsync(nameof(IWorkerNotifications.Begin), 0)),
        ("Worker releases rejected duplicate request buffers",
            () => RejectedBuffersAsync(nameof(IWorkerRpc.ExecuteAsync), 1, true)),
        ("Worker releases rejected full queue buffers",
            () => RejectedBuffersAsync(nameof(IWorkerRpc.ExecuteAsync), 32)),
        ("Worker releases rejected artwork limit buffers",
            () => RejectedBuffersAsync(nameof(IWorkerRpc.CopyArtworkAsync), PipeProtocol.MaximumArtworkRequests)),
        ("Canceled write closes a non-reading connection", BlockedWriteAsync),
        ("Caller cancellation preserves a partially written request frame",
            () => CallerFrameCancellationAsync("Execute")),
        ("Caller cancellation preserves a partially written activation frame",
            () => CallerFrameCancellationAsync("ActivateSource")),
        ("Cancellation while queued for the writer sends no frame", QueuedWriterCancellationAsync),
        ("Queue time does not shorten a frame's write budget", QueuedFrameBudgetAsync),
        ("A writer queue timeout leaves the active frame and pipe usable", WriterQueueTimeoutAsync),
        ("Frame writes honor a configured budget longer than five seconds", LongWriteBudgetAsync),
        ("Frame writes honor a configured short deadline", ShortWriteBudgetAsync),
        ("RPC write deadline completes the listening connection", RpcWriteDeadlineAsync),
        ("RPC worker rejects a repeated initialization", ReinitializeAsync),
        ("Full worker queue cannot delay owner shutdown", FullQueueShutdownAsync),
        ("Worker rejects duplicate admitted request IDs", () => InvalidRequestAsync(true)),
        ("Worker bounds canceled noncooperative work", () => InvalidRequestAsync(false)),
        ("Activation callback is revoked outside its command", ActivationContextAsync),
        ("Activation payloads cannot supply an executable path", ActivationPathIsOwnerLocalAsync)
    ];

    private static async Task InvalidLengthsAsync()
    {
        foreach (var length in new[] { 0, int.MinValue, int.MaxValue, -44, -65581 })
        {
            await WithPipesAsync(async (server, client) =>
            {
                using var reader = new TestFrames(client);
                var header = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(header, length);
                await server.WriteAsync(header).ConfigureAwait(false);
                await RejectAsync<InvalidDataException>(() => reader.ReadAsync(CancellationToken.None))
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
    }

    private static Task TruncatedFrameAsync() => WithPipesAsync(async (server, client) =>
    {
        using var reader = new TestFrames(client);
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(bytes, 128);
        await server.WriteAsync(bytes).ConfigureAwait(false);
        server.Dispose();
        await RejectAsync<EndOfStreamException>(() => reader.ReadAsync(CancellationToken.None)).ConfigureAwait(false);
    });

    private static Task InvalidJsonAsync() => WithPipesAsync(async (server, client) =>
    {
        using var reader = new TestFrames(client);
        await server.WriteAsync(new byte[] { 0, 0, 0, 1, 123 }).ConfigureAwait(false);
        await RejectAsync<JsonException>(() => reader.ReadAsync(CancellationToken.None)).ConfigureAwait(false);
    });

    private static Task BlockedWriteAsync() => WithPipesAsync(async (_, client) =>
    {
        using var writer = new TestFrames(client);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await RejectAsync<OperationCanceledException>(() =>
            writer.WriteAsync(Frame("Fault", new string('x', 2 * 1024 * 1024)), cancel.Token)).ConfigureAwait(false);
        await RejectAsync<ObjectDisposedException>(() => writer.WriteAsync(Frame("Shutdown"), CancellationToken.None))
            .ConfigureAwait(false);
    });

    private static Task BufferCompletionAsync() => WithPipesAsync(async (server, client) =>
    {
        var formatter = new BufferCheckingFormatter();
        using var receiver = new JsonRpc(new WorkerMessageHandler(client, formatter));
        var notices = new TestOwnerRpc();
        receiver.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IOwnerNotifications>(), notices, null);
        receiver.StartListening();
        using var sender = new WorkerMessageHandler(server, WorkerRpcEndpoint.CreateFormatter());
        await using var backend = new SyntheticBackend();
        var snapshot = await backend.ReadSnapshotAsync(default).ConfigureAwait(false);
        var epoch = Guid.NewGuid();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        for (var revision = 1; revision <= 2; revision++)
        {
            await sender
                .WriteAsync(
                    new JsonRpcRequest
                    {
                        Method = nameof(IOwnerNotifications.Snapshot),
                        ArgumentsList = [new WorkerSnapshot(epoch, snapshot with { Revision = revision })],
                        ArgumentListDeclaredTypes = [typeof(WorkerSnapshot)]
                    }, deadline.Token).ConfigureAwait(false);
            var received = await notices.Snapshots.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);
            HostingTests.Check(received.Epoch == epoch && received.Snapshot.Revision == revision &&
                               received.Snapshot.Sessions[0].MediaProperties.Title ==
                               snapshot.Sessions[0].MediaProperties.Title,
                "Releasing the frame buffers corrupted the dispatched snapshot.");
        }

        HostingTests.Check(await formatter.ReleasedBeforeNext.Task.WaitAsync(deadline.Token).ConfigureAwait(false),
            "The previous snapshot still retained its serialized arguments when the next frame was deserialized.");
    });

    private static Task RejectedBuffersAsync(string method, int admittedCount, bool duplicateId = false) =>
        WithPipesAsync(async (server, client) =>
        {
            var formatter = new BufferCheckingFormatter();
            using var receiver = new WorkerMessageHandler(client, formatter) { WorkerAdmission = true };
            using var sender = new WorkerMessageHandler(server, WorkerRpcEndpoint.CreateFormatter());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            for (var index = 0; index <= admittedCount; index++)
            {
                var frame = Frame(method, "payload");
                frame.RequestId = new(duplicateId ? 1 : index + 1);
                await sender.WriteAsync(frame, deadline.Token).ConfigureAwait(false);
                if (index < admittedCount)
                {
                    var admitted = (JsonRpcRequest)(await receiver.ReadAsync(deadline.Token).ConfigureAwait(false))!;
                    HostingTests.Check(Payload(admitted) == "payload",
                        "Admission released the request buffers before returning them.");
                    ((IJsonRpcMessageBufferManager)receiver).DeserializationComplete(admitted);
                }
                else
                {
                    await RejectAsync<InvalidDataException>(() => receiver.ReadAsync(deadline.Token).AsTask())
                        .ConfigureAwait(false);
                    HostingTests.Check(receiver.Failure is InvalidDataException,
                        "Rejection lost the admission failure.");
                    HostingTests.Check(formatter.LastRequest is { } rejected &&
                                       !rejected.TryGetArgumentByNameOrIndex(null, 0, typeof(string), out _),
                        "The rejected request still retained its serialized arguments.");
                }
            }
        });

    private static Task FullQueueShutdownAsync() => WithWorkerAsync(async (endpoint, worker, epoch, notices, _) =>
    {
        for (var id = 1; id <= 32; id++)
        {
            await endpoint.Handler.WriteAsync(HungCommand(epoch, id), default).ConfigureAwait(false);
        }

        await endpoint.Rpc.NotifyAsync(nameof(IWorkerNotifications.Shutdown), epoch).ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await worker.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
    });

    private static Task CallerFrameCancellationAsync(string kind) => WithPipesAsync(async (server, client) =>
    {
        using var writer = new TestFrames(client);
        using var reader = new TestFrames(server);
        using var caller = new CancellationTokenSource();
        var write = writer.WriteAsync(Frame(kind, new string('x', 2 * 1024 * 1024)), caller.Token, default);
        var header = new byte[4];
        await server.ReadExactlyAsync(header).ConfigureAwait(false);
        var payload = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
        await server.ReadExactlyAsync(payload.AsMemory(0, 1)).ConfigureAwait(false);
        caller.Cancel();
        HostingTests.Check(!write.IsCompleted, "The partial frame was canceled before it could finish.");
        await server.ReadExactlyAsync(payload.AsMemory(1)).ConfigureAwait(false);
        await write.ConfigureAwait(false);
        var message = (JsonRpcRequest)WorkerRpcEndpoint.CreateFormatter()
            .Deserialize(new ReadOnlySequence<byte>(payload));
        HostingTests.Check(message.Method == kind && Payload(message)?.Length == 2 * 1024 * 1024,
            "The canceled caller corrupted its frame.");
        await writer.WriteAsync(Frame("Shutdown"), default).ConfigureAwait(false);
        HostingTests.Check((await reader.ReadAsync(default).ConfigureAwait(false)).Method == "Shutdown",
            "The next frame was corrupted.");
    });

    private static Task QueuedWriterCancellationAsync() => WithPipesAsync(async (server, client) =>
    {
        using var writer = new TestFrames(client);
        using var reader = new TestFrames(server);
        using var caller = new CancellationTokenSource();
        var first = writer.WriteAsync(Frame("Fault", new string('x', 2 * 1024 * 1024)), default);
        var queued = writer.WriteAsync(Frame("Execute"), caller.Token, default);
        caller.Cancel();
        await RejectAsync<OperationCanceledException>(() => queued).ConfigureAwait(false);
        HostingTests.Check(!first.IsCompleted, "The first write was not blocked.");
        await reader.ReadAsync(default).ConfigureAwait(false);
        await first.ConfigureAwait(false);
        await writer.WriteAsync(Frame("Shutdown"), default).ConfigureAwait(false);
        HostingTests.Check((await reader.ReadAsync(default).ConfigureAwait(false)).Method == "Shutdown",
            "The canceled queued frame was sent.");
    });

    private static Task QueuedFrameBudgetAsync() => WithPipesAsync(async (server, client) =>
    {
        using var writer = new TestFrames(client) { WriteTimeout = TimeSpan.FromSeconds(2) };
        using var reader = new TestFrames(server);
        var first = writer.WriteAsync(Frame("Execute", new string('x', 2 * 1024 * 1024)), default);
        var header = new byte[4];
        await server.ReadExactlyAsync(header).ConfigureAwait(false);
        var payload = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
        await server.ReadExactlyAsync(payload.AsMemory(0, 1)).ConfigureAwait(false);
        var queued = writer.WriteAsync(Frame("ActivateSource", new string('y', 2 * 1024 * 1024)), default);
        await Task.Delay(1250).ConfigureAwait(false);
        await server.ReadExactlyAsync(payload.AsMemory(1)).ConfigureAwait(false);
        await first.ConfigureAwait(false);
        await server.ReadExactlyAsync(header).ConfigureAwait(false);
        payload = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
        await server.ReadExactlyAsync(payload.AsMemory(0, 1)).ConfigureAwait(false);
        await Task.Delay(1100).ConfigureAwait(false);
        HostingTests.Check(!queued.IsCompleted && !writer.IsClosed,
            "Queue time consumed the active frame's write budget.");
        await server.ReadExactlyAsync(payload.AsMemory(1)).ConfigureAwait(false);
        await queued.ConfigureAwait(false);
        await writer.WriteAsync(Frame("Shutdown"), default).ConfigureAwait(false);
        HostingTests.Check((await reader.ReadAsync(default).ConfigureAwait(false)).Method == "Shutdown",
            "A delayed frame corrupted the next one.");
    });

    private static Task WriterQueueTimeoutAsync() => WithPipesAsync(async (server, client) =>
    {
        using var writer = new TestFrames(client) { WriteTimeout = TimeSpan.FromSeconds(3) };
        using var reader = new TestFrames(server);
        var first = writer.WriteAsync(Frame("Execute", new string('x', 2 * 1024 * 1024)), default);
        var header = new byte[4];
        await server.ReadExactlyAsync(header).ConfigureAwait(false);
        var payload = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
        await server.ReadExactlyAsync(payload.AsMemory(0, 1)).ConfigureAwait(false);
        writer.WriteTimeout = TimeSpan.FromMilliseconds(100);
        await RejectAsync<OperationCanceledException>(() => writer.WriteAsync(Frame("Policy"), default))
            .ConfigureAwait(false);
        HostingTests.Check(!writer.IsClosed && !first.IsCompleted, "A queued timeout closed the active writer.");
        await server.ReadExactlyAsync(payload.AsMemory(1)).ConfigureAwait(false);
        await first.ConfigureAwait(false);
        await writer.WriteAsync(Frame("Shutdown"), default).ConfigureAwait(false);
        HostingTests.Check((await reader.ReadAsync(default).ConfigureAwait(false)).Method == "Shutdown",
            "An expired queued frame was sent.");
    });

    private static Task LongWriteBudgetAsync() => WithPipesAsync(async (server, client) =>
    {
        using var writer = new TestFrames(client) { WriteTimeout = TimeSpan.FromSeconds(8) };
        using var reader = new TestFrames(server);
        var write = writer.WriteAsync(Frame("Fault", new string('x', 2 * 1024 * 1024)), default);
        await Task.Delay(TimeSpan.FromMilliseconds(5500)).ConfigureAwait(false);
        HostingTests.Check(!write.IsCompleted, "The configured write budget was replaced with five seconds.");
        var message = await reader.ReadAsync(default).ConfigureAwait(false);
        await write.ConfigureAwait(false);
        HostingTests.Check(Payload(message)?.Length == 2 * 1024 * 1024, "The full delayed frame was not received.");
    });

    private static Task ShortWriteBudgetAsync() => WithPipesAsync(async (_, client) =>
    {
        using var writer = new TestFrames(client) { WriteTimeout = TimeSpan.FromMilliseconds(150) };
        await RejectAsync<TimeoutException>(() =>
            writer.WriteAsync(Frame("Fault", new string('x', 2 * 1024 * 1024)), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        await RejectAsync<ObjectDisposedException>(() => writer.WriteAsync(Frame("Shutdown"), default))
            .ConfigureAwait(false);
    });

    private static Task RpcWriteDeadlineAsync() => WithPipesAsync(async (_, client) =>
    {
        var handler = new WorkerMessageHandler(client, WorkerRpcEndpoint.CreateFormatter())
        {
            WriteTimeout = TimeSpan.FromMilliseconds(150)
        };
        using var rpc = new JsonRpc(handler);
        var worker = rpc.Attach<IWorkerRpc>();
        rpc.StartListening();
        var request = worker.ApplyPolicyAsync(Guid.NewGuid(), new(1, [new string('x', 2 * 1024 * 1024)]), default);
        await ((Task)request).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await rpc.Completion.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        HostingTests.Check(handler.IsClosed && rpc.Completion.IsCompleted && !request.IsCompletedSuccessfully,
            $"Failed frame: handler closed={handler.IsClosed}, RPC complete={rpc.Completion.IsCompleted}, request={request.Status}.");
        HostingTests.Check(handler.Failure is TimeoutException { InnerException: OperationCanceledException },
            $"The frame deadline lost its failure: {handler.Failure}");
    });

    private static Task ReinitializeAsync() => WithWorkerAsync(async (endpoint, process, _, _, hello) =>
    {
        var worker = endpoint.Rpc.Attach<IWorkerRpc>();
        var initialize = worker.InitializeAsync(hello, default);
        await ((Task)initialize).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
    });

    private static Task InvalidRequestAsync(bool duplicate) =>
        WithWorkerAsync(async (endpoint, worker, epoch, notices, _) =>
        {
            var count = duplicate ? 2 : 33;
            for (var index = 1; index <= count; index++)
            {
                var id = duplicate ? 1 : index;
                await endpoint.Handler.WriteAsync(HungCommand(epoch, id), default).ConfigureAwait(false);
                if (!duplicate && index <= 32)
                {
                    WorkerSnapshot notice;
                    do { notice = await notices.Snapshots.Reader.ReadAsync().ConfigureAwait(false); } while
                        (!notice.Snapshot.Sessions[0].MediaProperties.Subtitle
                             .Contains($"Commands: {index};", StringComparison.Ordinal));

                    await endpoint.Handler.WriteAsync(
                        new JsonRpcRequest
                        {
                            Method = "$/cancelRequest",
                            NamedArguments = new Dictionary<string, object?> { ["id"] = (long)id + 1000 },
                            NamedArgumentDeclaredTypes = new Dictionary<string, Type> { ["id"] = typeof(long) }
                        }, default).ConfigureAwait(false);
                }
            }

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await worker.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        });

    private static async Task ActivationContextAsync()
    {
        var calls = 0;
        var context = new HostedBackendContext(true, null, (_, _) =>
        {
            calls++;
            return Task.FromResult(true);
        });
        await RejectAsync<InvalidOperationException>(() =>
            context.TryActivateSourceAsync("spike.player", "title", CancellationToken.None)).ConfigureAwait(false);
        await using var backend = new SyntheticBackend("activation", context);
        var command = new MediaBackendCommand(new MediaBackendSessionId(1), 1, MediaOperation.ActivateSource, []);
        var result = await context.ExecuteAsync(backend, 42, command, CancellationToken.None).ConfigureAwait(false);
        HostingTests.Check(result.Status == MediaBackendCommandStatus.Completed && calls == 1,
            "An admitted callback was not executed exactly once.");
        await RejectAsync<InvalidOperationException>(() =>
            context.TryActivateSourceAsync("spike.player", "title", CancellationToken.None)).ConfigureAwait(false);
        await using var duplicate = new SyntheticBackend("duplicate-activation", context);
        await RejectAsync<InvalidOperationException>(() =>
            context.ExecuteAsync(duplicate, 43, command, CancellationToken.None)).ConfigureAwait(false);
        HostingTests.Check(calls == 2, "One command admitted duplicate activation callbacks.");
    }

    private static Task ActivationPathIsOwnerLocalAsync()
    {
        var activation = new WorkerActivation(Guid.NewGuid(), new(42, new(new(1), 1), "spike.player", "title")
        {
            ExecutablePath = @"C:\MediaControlsTests\owner.exe",
        });
        var json = JsonSerializer.Serialize(activation, WorkerJsonContext.Default.WorkerActivation);
        HostingTests.Check(!json.Contains("executablePath", StringComparison.Ordinal),
            "An owner-local executable path changed the activation wire contract.");
        var injected = json.Replace("\"applicationId\":", "\"executablePath\":\"worker.exe\",\"applicationId\":", StringComparison.Ordinal);
        var decoded = JsonSerializer.Deserialize(injected, WorkerJsonContext.Default.WorkerActivation)!;
        HostingTests.Check(decoded.Activation.ExecutablePath is null,
            "The worker supplied an executable path instead of using the owner's session snapshot.");
        return Task.CompletedTask;
    }

    private static JsonRpcRequest HungCommand(Guid epoch, int id) => new()
    {
        Method = nameof(IWorkerRpc.ExecuteAsync),
        RequestId = new(id + 1000),
        ArgumentsList =
        [
            new WorkerCommand(epoch, id,
                new MediaBackendCommand(new MediaBackendSessionId(1), 1, MediaOperation.SkipNext, []))
        ],
        ArgumentListDeclaredTypes = [typeof(WorkerCommand)]
    };

    private static async Task WithWorkerAsync(
        Func<WorkerRpcEndpoint, OwnedWorkerProcess, Guid, TestOwnerRpc, WorkerHello, Task> action)
    {
        var owner = Guid.NewGuid();
        var name = $"LOCAL\\MediaHostingTests.{owner:N}";
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var worker = OwnedWorkerProcess.Start(Environment.ProcessPath!, [
            "--worker", name, owner.ToString("D"),
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture), "hang-command"
        ]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
        await using var endpoint
            = await WorkerRpcEndpoint.CreateAsync(pipe, true, deadline.Token).ConfigureAwait(false);
        var notices = new TestOwnerRpc();
        endpoint.Rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IOwnerNotifications>(), notices, null);
        endpoint.Rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IOwnerRpc>(), notices, null);
        var rpc = endpoint.Rpc.Attach<IWorkerRpc>();
        endpoint.Rpc.StartListening();
        var hello = new WorkerHello(PipeProtocol.Version, owner, "hang-command", new SourcePolicyMessage(0, []), false,
            null,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(45));
        var welcome = await rpc.InitializeAsync(hello, deadline.Token).ConfigureAwait(false);
        await endpoint.Rpc.NotifyAsync(nameof(IWorkerNotifications.Begin), welcome.Epoch).ConfigureAwait(false);
        await notices.Snapshots.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);
        await action(endpoint, worker, welcome.Epoch, notices, hello).WaitAsync(deadline.Token).ConfigureAwait(false);
    }

    private static JsonRpcRequest Frame(string method, string payload = "") => new()
    {
        Method = method, RequestId = new(1), ArgumentsList = [payload], ArgumentListDeclaredTypes = [typeof(string)]
    };

    private static string? Payload(JsonRpcRequest request)
    {
        request.TryGetArgumentByNameOrIndex(null, 0, typeof(string), out var value);
        return value as string;
    }

    private static async Task WithPipesAsync(Func<NamedPipeServerStream, NamedPipeClientStream, Task> action)
    {
        var name = $"LOCAL\\MediaHostingTests.{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 4096);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(server.WaitForConnectionAsync(deadline.Token), client.ConnectAsync(deadline.Token))
            .ConfigureAwait(false);
        await action(server, client).WaitAsync(deadline.Token).ConfigureAwait(false);
    }

    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action().ConfigureAwait(false); }
        catch (T) { return; }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class TestFrames(Stream stream) : IDisposable
    {
        private readonly WorkerMessageHandler _handler = new(stream, WorkerRpcEndpoint.CreateFormatter());
        public bool IsClosed => this._handler.IsClosed;
        public TimeSpan WriteTimeout { get => this._handler.WriteTimeout; set => this._handler.WriteTimeout = value; }
        public void Dispose() => this._handler.Dispose();

        public async Task<JsonRpcRequest> ReadAsync(CancellationToken cancellationToken) =>
            (JsonRpcRequest)(await this._handler.ReadAsync(cancellationToken).ConfigureAwait(false))!;

        public Task WriteAsync(JsonRpcMessage message, CancellationToken lifetime) =>
            this.WriteAsync(message, lifetime, lifetime);

        public async Task WriteAsync(JsonRpcMessage message, CancellationToken admission, CancellationToken lifetime)
        {
            using var close = lifetime.Register(this.Dispose);
            await this._handler.WriteAsync(message, admission).ConfigureAwait(false);
        }
    }

    private sealed class BufferCheckingFormatter : IJsonRpcMessageFormatter, IJsonRpcInstanceContainer
    {
        private readonly SystemTextJsonFormatter _inner = WorkerRpcEndpoint.CreateFormatter();
        public JsonRpcRequest? LastRequest { get; private set; }

        public TaskCompletionSource<bool> ReleasedBeforeNext { get; }
            = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public JsonRpc Rpc { set => ((IJsonRpcInstanceContainer)this._inner).Rpc = value; }

        public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer)
        {
            if (this.LastRequest is { } previous)
            {
                this.ReleasedBeforeNext.TrySetResult(
                    !previous.TryGetArgumentByNameOrIndex(null, 0, typeof(WorkerSnapshot), out _));
            }

            var message = this._inner.Deserialize(contentBuffer);
            this.LastRequest = message as JsonRpcRequest;
            return message;
        }

        public void Serialize(IBufferWriter<byte> bufferWriter, JsonRpcMessage message) =>
            this._inner.Serialize(bufferWriter, message);

        public object GetJsonText(JsonRpcMessage message) => this._inner.GetJsonText(message);
    }
}