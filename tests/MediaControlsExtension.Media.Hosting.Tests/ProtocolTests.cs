using System.Buffers.Binary;
using System.Globalization;
using System.IO.Pipes;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting.Tests;

internal static class ProtocolTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("Frame parser rejects invalid signed lengths", InvalidLengthsAsync),
        ("Frame parser rejects a truncated payload", TruncatedFrameAsync),
        ("Frame parser rejects malformed JSON", InvalidJsonAsync),
        ("Canceled write closes a non-reading connection", BlockedWriteAsync),
        ("Caller cancellation preserves a partially written request frame", () => CallerFrameCancellationAsync(MessageKind.Execute)),
        ("Caller cancellation preserves a partially written activation frame", () => CallerFrameCancellationAsync(MessageKind.ActivateSource)),
        ("Cancellation while queued for the writer sends no frame", QueuedWriterCancellationAsync),
        ("Queue time does not shorten a frame's write budget", QueuedFrameBudgetAsync),
        ("A writer queue timeout leaves the active frame and pipe usable", WriterQueueTimeoutAsync),
        ("Frame writes honor a configured budget longer than five seconds", LongWriteBudgetAsync),
        ("Frame writes honor a configured short deadline", ShortWriteBudgetAsync),
        ("Full worker queue cannot delay owner shutdown", FullQueueShutdownAsync),
        ("Worker rejects duplicate admitted request IDs", () => InvalidRequestAsync(true)),
        ("Worker bounds canceled noncooperative work", () => InvalidRequestAsync(false)),
        ("Activation callback is revoked outside its command", ActivationContextAsync),
    ];

    private static async Task InvalidLengthsAsync()
    {
        foreach (var length in new[] { 0, int.MinValue, int.MaxValue, -44, -65581 })
        {
            await WithPipesAsync(async (server, client) =>
            {
                using var reader = new PipeProtocol(client);
                var header = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(header, length);
                await server.WriteAsync(header).ConfigureAwait(false);
                await RejectAsync<InvalidDataException>(() => reader.ReadAsync(CancellationToken.None)).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
    }

    private static Task TruncatedFrameAsync() => WithPipesAsync(async (server, client) =>
    {
        using var reader = new PipeProtocol(client);
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 128);
        await server.WriteAsync(bytes).ConfigureAwait(false);
        server.Dispose();
        await RejectAsync<EndOfStreamException>(() => reader.ReadAsync(CancellationToken.None)).ConfigureAwait(false);
    });

    private static Task InvalidJsonAsync() => WithPipesAsync(async (server, client) =>
    {
        using var reader = new PipeProtocol(client);
        await server.WriteAsync(new byte[] { 1, 0, 0, 0, 123 }).ConfigureAwait(false);
        await RejectAsync<System.Text.Json.JsonException>(() => reader.ReadAsync(CancellationToken.None)).ConfigureAwait(false);
    });

    private static Task BlockedWriteAsync() => WithPipesAsync(async (_, client) =>
    {
        using var writer = new PipeProtocol(client);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await RejectAsync<OperationCanceledException>(() => writer.WriteAsync(new(MessageKind.Fault, Guid.NewGuid())
        {
            Error = new string('x', 2 * 1024 * 1024),
        }, cancel.Token)).ConfigureAwait(false);
        await RejectAsync<ObjectDisposedException>(() => writer.WriteAsync(new(MessageKind.Shutdown, Guid.NewGuid()), CancellationToken.None)).ConfigureAwait(false);
    });

    private static Task FullQueueShutdownAsync() => WithWorkerAsync(async (protocol, worker, owner, epoch) =>
    {
        for (var id = 1; id <= 32; id++)
        {
            await protocol.WriteAsync(HungCommand(owner, epoch, id), CancellationToken.None).ConfigureAwait(false);
        }

        await protocol.WriteAsync(new(MessageKind.Shutdown, owner, epoch), CancellationToken.None).ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await worker.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
    });

    private static Task CallerFrameCancellationAsync(MessageKind kind) => WithPipesAsync(async (server, client) =>
    {
        using var writer = new PipeProtocol(client);
        using var reader = new PipeProtocol(server);
        using var caller = new CancellationTokenSource();
        var write = writer.WriteAsync(new(kind, Guid.NewGuid()) { Error = new string('x', 2 * 1024 * 1024) }, caller.Token, default);
        var header = new byte[4];
        await server.ReadExactlyAsync(header).ConfigureAwait(false);
        var payload = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
        await server.ReadExactlyAsync(payload.AsMemory(0, 1)).ConfigureAwait(false);
        caller.Cancel();
        HostingTests.Check(!write.IsCompleted, "The partial frame was canceled before it could finish.");
        await server.ReadExactlyAsync(payload.AsMemory(1)).ConfigureAwait(false);
        await write.ConfigureAwait(false);
        var message = System.Text.Json.JsonSerializer.Deserialize(payload, WireJsonContext.Default.WireMessage);
        HostingTests.Check(message?.Kind == kind && message.Error?.Length == 2 * 1024 * 1024, "The canceled caller corrupted its frame.");
        await writer.WriteAsync(new(MessageKind.Shutdown, Guid.NewGuid()), default).ConfigureAwait(false);
        HostingTests.Check((await reader.ReadAsync(default).ConfigureAwait(false)).Kind == MessageKind.Shutdown, "The next frame was corrupted.");
    });

    private static Task QueuedWriterCancellationAsync() => WithPipesAsync(async (server, client) =>
    {
        using var writer = new PipeProtocol(client);
        using var reader = new PipeProtocol(server);
        using var caller = new CancellationTokenSource();
        var first = writer.WriteAsync(new(MessageKind.Fault, Guid.NewGuid()) { Error = new string('x', 2 * 1024 * 1024) }, default);
        var queued = writer.WriteAsync(new(MessageKind.Execute, Guid.NewGuid()), caller.Token, default);
        caller.Cancel();
        await RejectAsync<OperationCanceledException>(() => queued).ConfigureAwait(false);
        HostingTests.Check(!first.IsCompleted, "The first write was not blocked.");
        await reader.ReadAsync(default).ConfigureAwait(false);
        await first.ConfigureAwait(false);
        await writer.WriteAsync(new(MessageKind.Shutdown, Guid.NewGuid()), default).ConfigureAwait(false);
        HostingTests.Check((await reader.ReadAsync(default).ConfigureAwait(false)).Kind == MessageKind.Shutdown, "The canceled queued frame was sent.");
    });

    private static Task QueuedFrameBudgetAsync() => WithPipesAsync(async (server, client) =>
    {
        using var writer = new PipeProtocol(client) { WriteTimeout = TimeSpan.FromSeconds(2) };
        using var reader = new PipeProtocol(server);
        var first = writer.WriteAsync(new(MessageKind.Execute, Guid.NewGuid()) { Error = new string('x', 2 * 1024 * 1024) }, default);
        var header = new byte[4];
        await server.ReadExactlyAsync(header).ConfigureAwait(false);
        var payload = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
        await server.ReadExactlyAsync(payload.AsMemory(0, 1)).ConfigureAwait(false);
        var queued = writer.WriteAsync(new(MessageKind.ActivateSource, Guid.NewGuid()) { Error = new string('y', 2 * 1024 * 1024) }, default);
        await Task.Delay(1250).ConfigureAwait(false);
        await server.ReadExactlyAsync(payload.AsMemory(1)).ConfigureAwait(false);
        await first.ConfigureAwait(false);
        await server.ReadExactlyAsync(header).ConfigureAwait(false);
        payload = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
        await server.ReadExactlyAsync(payload.AsMemory(0, 1)).ConfigureAwait(false);
        await Task.Delay(1100).ConfigureAwait(false);
        HostingTests.Check(!queued.IsCompleted && !writer.IsClosed, "Queue time consumed the active frame's write budget.");
        await server.ReadExactlyAsync(payload.AsMemory(1)).ConfigureAwait(false);
        await queued.ConfigureAwait(false);
        await writer.WriteAsync(new(MessageKind.Shutdown, Guid.NewGuid()), default).ConfigureAwait(false);
        HostingTests.Check((await reader.ReadAsync(default).ConfigureAwait(false)).Kind == MessageKind.Shutdown, "A delayed frame corrupted the next one.");
    });

    private static Task WriterQueueTimeoutAsync() => WithPipesAsync(async (server, client) =>
    {
        using var writer = new PipeProtocol(client) { WriteTimeout = TimeSpan.FromSeconds(3) };
        using var reader = new PipeProtocol(server);
        var first = writer.WriteAsync(new(MessageKind.Execute, Guid.NewGuid()) { Error = new string('x', 2 * 1024 * 1024) }, default);
        var header = new byte[4];
        await server.ReadExactlyAsync(header).ConfigureAwait(false);
        var payload = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
        await server.ReadExactlyAsync(payload.AsMemory(0, 1)).ConfigureAwait(false);
        writer.WriteTimeout = TimeSpan.FromMilliseconds(100);
        await RejectAsync<OperationCanceledException>(() => writer.WriteAsync(new(MessageKind.Policy, Guid.NewGuid()), default)).ConfigureAwait(false);
        HostingTests.Check(!writer.IsClosed && !first.IsCompleted, "A queued timeout closed the active writer.");
        await server.ReadExactlyAsync(payload.AsMemory(1)).ConfigureAwait(false);
        await first.ConfigureAwait(false);
        await writer.WriteAsync(new(MessageKind.Shutdown, Guid.NewGuid()), default).ConfigureAwait(false);
        HostingTests.Check((await reader.ReadAsync(default).ConfigureAwait(false)).Kind == MessageKind.Shutdown, "An expired queued frame was sent.");
    });

    private static Task LongWriteBudgetAsync() => WithPipesAsync(async (server, client) =>
    {
        using var writer = new PipeProtocol(client) { WriteTimeout = TimeSpan.FromSeconds(8) };
        using var reader = new PipeProtocol(server);
        var write = writer.WriteAsync(new(MessageKind.Fault, Guid.NewGuid()) { Error = new string('x', 2 * 1024 * 1024) }, default);
        await Task.Delay(TimeSpan.FromMilliseconds(5500)).ConfigureAwait(false);
        HostingTests.Check(!write.IsCompleted, "The configured write budget was replaced with five seconds.");
        var message = await reader.ReadAsync(default).ConfigureAwait(false);
        await write.ConfigureAwait(false);
        HostingTests.Check(message.Error?.Length == 2 * 1024 * 1024, "The full delayed frame was not received.");
    });

    private static Task ShortWriteBudgetAsync() => WithPipesAsync(async (_, client) =>
    {
        using var writer = new PipeProtocol(client) { WriteTimeout = TimeSpan.FromMilliseconds(150) };
        await RejectAsync<OperationCanceledException>(() => writer.WriteAsync(new(MessageKind.Fault, Guid.NewGuid())
        {
            Error = new string('x', 2 * 1024 * 1024),
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        await RejectAsync<ObjectDisposedException>(() => writer.WriteAsync(new(MessageKind.Shutdown, Guid.NewGuid()), default)).ConfigureAwait(false);
    });

    private static Task InvalidRequestAsync(bool duplicate) => WithWorkerAsync(async (protocol, worker, owner, epoch) =>
    {
        var count = duplicate ? 2 : 33;
        for (var index = 1; index <= count; index++)
        {
            var id = duplicate ? 1 : index;
            await protocol.WriteAsync(HungCommand(owner, epoch, id), CancellationToken.None).ConfigureAwait(false);
            if (!duplicate && index <= 32)
            {
                WireMessage observed;
                do
                {
                    observed = await protocol.ReadAsync(CancellationToken.None).ConfigureAwait(false);
                }
                while (observed.Snapshot?.Sessions[0].MediaProperties.Subtitle.Contains($"Commands: {index};", StringComparison.Ordinal) != true);
                await protocol.WriteAsync(new(MessageKind.Cancel, owner, epoch, id), CancellationToken.None).ConfigureAwait(false);
            }
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await worker.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
    });

    private static async Task ActivationContextAsync()
    {
        var calls = 0;
        var context = new HostedBackendContext(true, null, (_, _) => { calls++; return Task.FromResult(true); });
        await RejectAsync<InvalidOperationException>(() => context.TryActivateSourceAsync("spike.player", "title", CancellationToken.None)).ConfigureAwait(false);
        await using var backend = new SyntheticBackend("activation", context);
        var command = new MediaBackendCommand(new(1), 1, MediaOperation.ActivateSource, []);
        var result = await context.ExecuteAsync(backend, 42, command, CancellationToken.None).ConfigureAwait(false);
        HostingTests.Check(result.Status == MediaBackendCommandStatus.Completed && calls == 1, "An admitted callback was not executed exactly once.");
        await RejectAsync<InvalidOperationException>(() => context.TryActivateSourceAsync("spike.player", "title", CancellationToken.None)).ConfigureAwait(false);
        await using var duplicate = new SyntheticBackend("duplicate-activation", context);
        await RejectAsync<InvalidOperationException>(() => context.ExecuteAsync(duplicate, 43, command, CancellationToken.None)).ConfigureAwait(false);
        HostingTests.Check(calls == 2, "One command admitted duplicate activation callbacks.");
    }

    private static WireMessage HungCommand(Guid owner, Guid epoch, int id) => new(MessageKind.Execute, owner, epoch, id)
    {
        Command = new(new(1), 1, MediaOperation.SkipNext, []),
    };

    private static async Task WithWorkerAsync(Func<PipeProtocol, OwnedWorkerProcess, Guid, Guid, Task> action)
    {
        var owner = Guid.NewGuid();
        var name = $"LOCAL\\MediaHostingTests.{owner:N}";
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var worker = OwnedWorkerProcess.Start(Environment.ProcessPath!, ["--worker", name, owner.ToString("D"),
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture), "hang-command"]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
        using var protocol = new PipeProtocol(pipe);
        await protocol.WriteAsync(new(MessageKind.Hello, owner)
        {
            BackendId = "hang-command",
            Policy = new(0, []),
        }, deadline.Token).ConfigureAwait(false);
        var welcome = await protocol.ReadAsync(deadline.Token).ConfigureAwait(false);
        await protocol.ReadAsync(deadline.Token).ConfigureAwait(false);
        await action(protocol, worker, owner, welcome.Epoch).WaitAsync(deadline.Token).ConfigureAwait(false);
    }

    private static async Task WithPipesAsync(Func<NamedPipeServerStream, NamedPipeClientStream, Task> action)
    {
        var name = $"LOCAL\\MediaHostingTests.{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(server.WaitForConnectionAsync(deadline.Token), client.ConnectAsync(deadline.Token)).ConfigureAwait(false);
        await action(server, client).WaitAsync(deadline.Token).ConfigureAwait(false);
    }

    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action().ConfigureAwait(false); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}