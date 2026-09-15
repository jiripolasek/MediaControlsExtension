using System.Buffers;
using System.Buffers.Binary;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting;

// JSON-RPC framing with bounded allocation and independent queue/write deadlines.
internal sealed class WorkerMessageHandler(Stream stream, IJsonRpcMessageFormatter formatter)
    : IJsonRpcMessageHandler, IJsonRpcMessageBufferManager, Microsoft.IDisposableObservable
{
    internal static readonly AsyncLocal<SendScope?> CurrentSend = new();
    private readonly Lock _admissionGate = new();
    private readonly CancellationTokenSource _closed = new();
    private readonly Dictionary<RequestId, bool> _requests = [];
    private readonly SemaphoreSlim _writer = new(1, 1);
    private int _artworkRequests;
    private Exception? _failure;

    public IJsonRpcMessageFormatter Formatter { get; } = formatter;
    public bool CanRead => true;
    public bool CanWrite => true;
    public bool IsClosed => this._closed.IsCancellationRequested;
    public bool IsDisposed => this.IsClosed;
    public Exception? Failure => Volatile.Read(ref this._failure);
    public bool WorkerAdmission { get; init; }
    public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public async ValueTask<JsonRpcMessage?> ReadAsync(CancellationToken cancellationToken)
    {
        using var read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this._closed.Token);
        cancellationToken = read.Token;
        JsonRpcMessage? message = null;
        try
        {
            var header = new byte[4];
            if (await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) == 0)
            {
                return null;
            }

            await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadInt32BigEndian(header);
            if (length is <= 0 or > PipeProtocol.MaximumFrameBytes)
            {
                throw new InvalidDataException("Invalid worker frame length.");
            }

            var payload = new byte[length];
            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            message = this.Formatter.Deserialize(new ReadOnlySequence<byte>(payload));
            if (this.WorkerAdmission && message is JsonRpcRequest request && request.Method != "$/cancelRequest")
            {
                var control = request.Method is nameof(IWorkerNotifications.Begin)
                    or nameof(IWorkerNotifications.Shutdown);
                var initialize = request.Method == nameof(IWorkerRpc.InitializeAsync);
                var artwork = request.Method == nameof(IWorkerRpc.CopyArtworkAsync);
                if (control != request.IsNotification || !control && !initialize && !artwork &&
                    request.Method is not (nameof(IWorkerRpc.ExecuteAsync) or nameof(IWorkerRpc.ApplyPolicyAsync)
                        or nameof(IWorkerRpc.InvalidateAsync)))
                {
                    throw new InvalidDataException("Invalid worker RPC method or notification.");
                }

                if (!control)
                {
                    lock (this._admissionGate)
                    {
                        if (this._requests.Count >= 32 || this._requests.ContainsKey(request.RequestId) ||
                            artwork && this._artworkRequests >= PipeProtocol.MaximumArtworkRequests)
                        {
                            throw new InvalidDataException("Duplicate RPC ID or worker admission limit exceeded.");
                        }

                        this._requests.Add(request.RequestId, artwork);
                        if (artwork) { this._artworkRequests++; }
                    }
                }
            }

            return message;
        }
        catch (Exception ex)
        {
            if (!read.IsCancellationRequested) { Interlocked.CompareExchange(ref this._failure, ex, null); }

            if (message is not null) { ((IJsonRpcMessageBufferManager)this).DeserializationComplete(message); }

            throw;
        }
    }

    void IJsonRpcMessageBufferManager.DeserializationComplete(JsonRpcMessage message) =>
        (message as IJsonRpcMessageBufferManager)?.DeserializationComplete(message);

    public async ValueTask WriteAsync(JsonRpcMessage message, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(this.IsClosed, this);
        var scope = message is JsonRpcRequest { IsResponseExpected: true } ? CurrentSend.Value : null;
        using var queue = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this._closed.Token,
            scope?.Cancellation ?? default);
        queue.CancelAfter(this.WriteTimeout);
        await this._writer.WaitAsync(queue.Token).ConfigureAwait(false);
        try
        {
            queue.Token.ThrowIfCancellationRequested();
            var buffer = new ArrayBufferWriter<byte>();
            this.Formatter.Serialize(buffer, message);
            if (buffer.WrittenCount > PipeProtocol.MaximumFrameBytes)
            {
                throw new InvalidDataException("Worker frame exceeds the size limit.");
            }

            var header = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, buffer.WrittenCount);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(this._closed.Token);
            deadline.CancelAfter(this.WriteTimeout);
            try
            {
                if (message is IJsonRpcMessageWithId response && message is not JsonRpcRequest)
                {
                    lock (this._admissionGate)
                    {
                        if (this._requests.Remove(response.RequestId, out var artwork) && artwork)
                        {
                            this._artworkRequests--;
                        }
                    }
                }

                await stream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
                await stream.WriteAsync(buffer.WrittenMemory, deadline.Token).ConfigureAwait(false);
                await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
                scope?.Sent();
            }
            catch (OperationCanceledException ex) when (deadline.IsCancellationRequested && !this.IsClosed)
            {
                var failure = new TimeoutException("The worker frame write timed out.", ex);
                Interlocked.CompareExchange(ref this._failure, failure, null);
                this.Dispose();
                throw failure;
            }
            catch (Exception ex)
            {
                if (!this.IsClosed) { Interlocked.CompareExchange(ref this._failure, ex, null); }

                this.Dispose();
                throw;
            }
        }
        finally { this._writer.Release(); }
    }

    public void Dispose()
    {
        this._closed.Cancel();
        stream.Dispose();
    }

    internal sealed record SendScope(Action Sent, CancellationToken Cancellation);
}