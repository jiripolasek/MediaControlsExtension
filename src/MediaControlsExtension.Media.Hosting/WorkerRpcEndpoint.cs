using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting;

internal sealed class WorkerRpcEndpoint(MultiplexingStream multiplexing, WorkerMessageHandler handler)
    : IDisposable, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private Task? _disposal;
    public WorkerMessageHandler Handler { get; } = handler;
    public JsonRpc Rpc { get; } = new(handler) { CancelLocallyInvokedMethodsWhenConnectionIsClosed = true };
    public bool IsClosed => this.Rpc.IsDisposed || this.Handler.IsClosed;

    public async ValueTask DisposeAsync()
    {
        this.Dispose();
        await this._disposal!.ConfigureAwait(false);
    }

    public void Dispose()
    {
        this.Rpc.Dispose();
        lock (this._gate) { this._disposal ??= multiplexing.DisposeAsync().AsTask(); }
    }

    public static async Task<WorkerRpcEndpoint> CreateAsync(
        Stream pipe,
        bool owner,
        CancellationToken cancellationToken)
    {
        var multiplexing = await MultiplexingStream.CreateAsync(pipe,
            new MultiplexingStream.Options
            {
                ProtocolMajorVersion = 2, DefaultChannelReceivingWindowSize = PipeProtocol.MaximumChunkBytes
            }, cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = owner
                ? await multiplexing.OfferChannelAsync("rpc", cancellationToken: cancellationToken)
                    .ConfigureAwait(false)
                : await multiplexing.AcceptChannelAsync("rpc", cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            return new WorkerRpcEndpoint(multiplexing,
                new WorkerMessageHandler(channel.AsStream(), CreateFormatter(multiplexing))
                {
                    WorkerAdmission = !owner
                });
        }
        catch
        {
            await multiplexing.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Wire DTOs use generated metadata and streams use the library converter.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Wire DTOs use generated metadata and streams use the library converter.")]
    internal static SystemTextJsonFormatter CreateFormatter(MultiplexingStream? multiplexing = null) => new()
    {
        MultiplexingStream = multiplexing,
        JsonSerializerOptions =
        {
            TypeInfoResolver
                = JsonTypeInfoResolver.Combine(WorkerJsonContext.Default, new RpcTypeInfoResolver())
        }
    };
}

internal sealed class RpcTypeInfoResolver : IJsonTypeInfoResolver
{
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        if (type == typeof(RequestId))
        {
            // RequestId's internal converter cannot be referenced by our source generator.
            foreach (var converter in options.Converters)
            {
                if (converter is JsonConverter<RequestId> requestIdConverter)
                {
                    return JsonMetadataServices.CreateValueInfo<RequestId>(options, requestIdConverter);
                }
            }
        }

        return null;
    }
}