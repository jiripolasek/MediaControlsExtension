using System.Buffers.Binary;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting.Tests;

internal sealed class ReviewPartialRequestPeer : TestWorkerRpc
{
    public static async Task RunAsync(string pipeName, int processId, string releaseName)
    {
        using var release = EventWaitHandle.OpenExisting(releaseName);
        await using var peer = new ReviewPartialRequestPeer();
        await RunAsync(pipeName, processId, peer, stream => new PartialReadStream(stream, async () =>
        {
            await peer.PublishAsync("Partial frame", 1).ConfigureAwait(false);
            if (!await Task.Run(() => release.WaitOne(TimeSpan.FromSeconds(5))).ConfigureAwait(false))
            {
                throw new TimeoutException("The owner did not release its partial frame.");
            }
        })).ConfigureAwait(false);
    }

    public override async Task<long> ApplyPolicyAsync(
        Guid epoch,
        SourcePolicyMessage policy,
        CancellationToken cancellationToken)
    {
        HostingTests.Check(policy.ExcludedApplicationIds[0].Length == 2 * 1024 * 1024,
            "The canceled caller corrupted its frame.");
        await this.PublishAsync("Frame received", policy.Revision).ConfigureAwait(false);
        return policy.Revision;
    }

    public override async Task<MediaBackendCommandResult> ExecuteAsync(
        WorkerCommand request,
        CancellationToken cancellationToken)
    {
        await this.Endpoint.Rpc.NotifyAsync(nameof(IOwnerNotifications.SnapshotFailed),
            new WorkerSnapshotFailure(this.Epoch, 0, "Obsolete read failure")).ConfigureAwait(false);
        return new MediaBackendCommandResult(MediaBackendCommandStatus.Completed, null);
    }

    internal sealed class PartialReadStream(Stream inner, Func<Task> pause) : Stream
    {
        private readonly byte[] _header = new byte[4];
        private int _headerBytes;
        private bool _paused;
        private int _remaining;
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override ValueTask
            WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var isHeader = this._remaining == 0;
            var hold = !isHeader && this._remaining > 1024 * 1024 && !this._paused;
            var count = await inner
                .ReadAsync(
                    buffer[..Math.Min(buffer.Length, isHeader ? 4 - this._headerBytes : hold ? 1 : this._remaining)],
                    cancellationToken).ConfigureAwait(false);
            if (isHeader)
            {
                buffer.Span[..count].CopyTo(this._header.AsSpan(this._headerBytes));
                this._headerBytes += count;
                if (this._headerBytes == 4)
                {
                    this._remaining = BinaryPrimitives.ReadInt32BigEndian(this._header);
                    this._headerBytes = 0;
                    this._paused = false;
                }
            }
            else { this._remaining -= count; }

            if (hold)
            {
                this._paused = true;
                await pause().ConfigureAwait(false);
            }

            return count;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); }

            base.Dispose(disposing);
        }
    }
}