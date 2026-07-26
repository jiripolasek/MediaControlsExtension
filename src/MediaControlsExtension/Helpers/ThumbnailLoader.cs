// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal static class ThumbnailLoader
{
    public static async Task<ThumbnailInfo?> LoadAsync(
        MediaArtworkContent content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        if (content.Data.IsEmpty)
        {
            return null;
        }

        var stream = new InMemoryRandomAccessStream();
        try
        {
            var buffer = MemoryMarshal.TryGetArray(
                content.Data,
                out ArraySegment<byte> segment)
                ? segment.Array!.AsBuffer(segment.Offset, segment.Count)
                : content.Data.ToArray().AsBuffer();
            await stream.WriteAsync(buffer).AsTask(cancellationToken);
            stream.Seek(0);
            return new ThumbnailInfo(content.Hash, content.ContentType, stream);
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }
}
