// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;

namespace JPSoftworks.MediaControlsExtension.Model;

internal sealed record ThumbnailInfo(string? Hash, IRandomAccessStream? Stream)
{
    public async Task<string?> GetDataUriAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (this.Stream is null)
        {
            return null;
        }

        using var stream = this.Stream.CloneStream();
        if (stream.Size == 0 || stream.Size > int.MaxValue)
        {
            return null;
        }

        var bytes = new byte[(int)stream.Size];
        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)stream.Size).AsTask(cancellationToken);
            reader.ReadBytes(bytes);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return $"data:{DetectContentType(bytes)};base64,{Convert.ToBase64String(bytes)}";
    }

    private static string DetectContentType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return "image/png";
        }

        if (bytes.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }))
        {
            return "image/jpeg";
        }

        if (bytes.StartsWith("GIF8"u8))
        {
            return "image/gif";
        }

        if (bytes.StartsWith("BM"u8))
        {
            return "image/bmp";
        }

        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        return "application/octet-stream";
    }
}
