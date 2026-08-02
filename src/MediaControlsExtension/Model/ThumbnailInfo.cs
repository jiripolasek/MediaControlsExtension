// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace JPSoftworks.MediaControlsExtension.Model;

internal sealed class ThumbnailInfo
{
    private readonly Lock _iconLock = new();
    private IconInfo? _icon;
    private int _released;

    public ThumbnailInfo(
        string? hash,
        string contentType,
        IRandomAccessStream? stream)
    {
        this.Hash = hash;
        this.ContentType = contentType;
        this.Stream = stream;
    }

    public string? Hash { get; }

    public string ContentType { get; }

    public IRandomAccessStream? Stream { get; }

    public IconInfo? GetIcon()
    {
        if (this.Stream is null)
        {
            return null;
        }

        lock (this._iconLock)
        {
            return this._icon ??= IconInfo.FromStream(this.Stream);
        }
    }

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

        string base64;
        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            var loaded = await reader.LoadAsync((uint)stream.Size).AsTask(cancellationToken);
            if (loaded == 0)
            {
                return null;
            }

            base64 = CryptographicBuffer.EncodeToBase64String(
                reader.ReadBuffer(loaded));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return $"data:{this.ContentType};base64,{base64}";
    }

    /// <summary>
    /// Releases a thumbnail that was loaded but never published to presentation code.
    /// Published thumbnails may still back an <see cref="IconInfo"/> and must not be
    /// disposed through this path.
    /// </summary>
    public void DisposeUnpublished()
    {
        if (Interlocked.Exchange(ref this._released, 1) == 0)
        {
            this.Stream?.Dispose();
        }
    }
}
