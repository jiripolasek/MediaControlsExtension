using System.Buffers.Binary;
using System.Collections.Immutable;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting;

internal enum MessageKind
{
    Hello, Welcome, Snapshot, Execute, Artwork, Invalidate, Policy, Cancel, Shutdown, Reply, Fault,
    ArtworkStart, ArtworkChunk, ArtworkEnd, ActivateSource, ActivationReply, SnapshotFailure,
}

internal sealed record SourcePolicyMessage(long Revision, string[] ExcludedApplicationIds)
{
    public static SourcePolicyMessage FromPolicy(MediaBackendSourcePolicy policy) => new(policy.Revision, [.. policy.ExcludedApplicationIds]);
    public MediaBackendSourcePolicy ToPolicy() => new(this.Revision, this.ExcludedApplicationIds);
}

internal sealed record WireMessage(MessageKind Kind, Guid Owner, Guid Epoch = default, long RequestId = 0)
{
    public int Version { get; init; } = PipeProtocol.Version;
    public string? BackendId { get; init; }
    public SourcePolicyMessage? Policy { get; init; }
    public MediaBackendSnapshot? Snapshot { get; init; }
    public MediaBackendCommand? Command { get; init; }
    public MediaBackendCommandResult? Result { get; init; }
    public MediaArtworkKey? ArtworkKey { get; init; }
    public MediaArtworkContent? Artwork { get; init; }
    public ImmutableArray<MediaBackendObservationRequest> Invalidations { get; init; } = [];
    public string? Error { get; init; }
    public long SourcePolicyRevision { get; init; }
    public bool CanActivateSource { get; init; }
    public WorkerLoggingOptions? Logging { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ObservationTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan PolicyTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public HostedSourceActivation? Activation { get; init; }
    public bool Activated { get; init; }
    public int ArtworkLength { get; init; }
    public int Offset { get; init; }
    [JsonIgnore]
    public byte[]? Chunk { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WireMessage))]
internal sealed partial class WireJsonContext : JsonSerializerContext;

internal sealed partial class PipeProtocol(PipeStream pipe) : IDisposable
{
    internal const int Version = 5;
    internal const int MaximumFrameBytes = 16 * 1024 * 1024;
    internal const int MaximumArtworkBytes = 32 * 1024 * 1024;
    internal const int MaximumChunkBytes = 64 * 1024;
    internal const int MaximumArtworkRequests = 2;
    private const int ChunkHeaderBytes = 44;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly PipeStream _pipe = pipe;
    private int _disposed;

    public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public bool IsClosed => Volatile.Read(ref this._disposed) != 0;

    public async Task<WireMessage> ReadAsync(CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await this._pipe.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        var binary = length < 0;
        if (length == int.MinValue || length == 0 || length > MaximumFrameBytes ||
            binary && (-length <= ChunkHeaderBytes || -length > MaximumChunkBytes + ChunkHeaderBytes))
        {
            throw new InvalidDataException("Invalid worker frame length.");
        }

        var payload = new byte[Math.Abs(length)];
        await this._pipe.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        if (binary)
        {
            return new(MessageKind.ArtworkChunk, new Guid(payload.AsSpan(0, 16)), new Guid(payload.AsSpan(16, 16)),
                BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(32, 8)))
            {
                Offset = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(40, 4)),
                Chunk = payload[ChunkHeaderBytes..],
            };
        }

        return JsonSerializer.Deserialize(payload, WireJsonContext.Default.WireMessage)
            ?? throw new InvalidDataException("Missing worker message.");
    }

    public Task WriteAsync(WireMessage message, CancellationToken lifetimeToken) => this.WriteAsync(message, lifetimeToken, lifetimeToken);

    public async Task WriteAsync(WireMessage message, CancellationToken admissionToken, CancellationToken lifetimeToken)
    {
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(admissionToken, lifetimeToken);
        admission.CancelAfter(this.WriteTimeout);
        admission.Token.ThrowIfCancellationRequested();
        byte[] payload;
        var binary = message.Kind == MessageKind.ArtworkChunk;
        if (binary)
        {
            if (message.Chunk is not { Length: > 0 and <= MaximumChunkBytes } chunk)
            {
                throw new InvalidDataException("Invalid artwork chunk.");
            }

            payload = new byte[ChunkHeaderBytes + chunk.Length];
            message.Owner.TryWriteBytes(payload.AsSpan(0, 16));
            message.Epoch.TryWriteBytes(payload.AsSpan(16, 16));
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(32, 8), message.RequestId);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(40, 4), message.Offset);
            chunk.CopyTo(payload, ChunkHeaderBytes);
        }
        else
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(message, WireJsonContext.Default.WireMessage);
        }
        if (payload.Length > MaximumFrameBytes)
        {
            throw new InvalidDataException("Worker frame exceeds the size limit.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, binary ? -payload.Length : payload.Length);
        await this._writer.WaitAsync(admission.Token).ConfigureAwait(false);
        try
        {
            admission.Token.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            deadline.CancelAfter(this.WriteTimeout);
            try
            {
                await this._pipe.WriteAsync(header, deadline.Token).ConfigureAwait(false);
                await this._pipe.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
                await this._pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
            }
            catch
            {
                this.Dispose();
                throw;
            }
        }
        finally
        {
            this._writer.Release();
        }
    }

    public static void VerifyPeer(PipeStream pipe, int expectedProcessId, bool server)
    {
        uint processId;
        var result = server
            ? GetNamedPipeClientProcessId(pipe.SafePipeHandle, out processId)
            : GetNamedPipeServerProcessId(pipe.SafePipeHandle, out processId);
        if (result == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        if (processId != expectedProcessId)
        {
            throw new InvalidDataException("The worker pipe belongs to a different process.");
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref this._disposed, 1);
        this._pipe.Dispose();
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}