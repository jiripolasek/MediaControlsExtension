// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal static class ThumbnailLoader
{
    public static async Task<ThumbnailInfo?> LoadAsync(
     IRandomAccessStreamReference? reference,
     uint maxWidth = 0,
     uint maxHeight = 0,
     CancellationToken cancellationToken = default)
    {
        if (reference == null)
        {
            return null;
        }

        IRandomAccessStream? stream = null;
        InMemoryRandomAccessStream? scaledStream = null;
        SoftwareBitmap? bitmap = null;

        try
        {
            stream = await reference.OpenReadAsync().AsTask(cancellationToken);
            if (stream.Size == 0)
            {
                stream.Dispose();
                return null;
            }

            // Buffer for hashing
            byte[] buffer;
            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)stream.Size);
                buffer = new byte[stream.Size];
                reader.ReadBytes(buffer);
            }
            string hash = ComputeHash(buffer);

            stream.Seek(0); // rewind for decoder

            if ((maxWidth > 0 || maxHeight > 0))
            {
                var decoder = await BitmapDecoder.CreateAsync(stream);
                uint width = decoder.PixelWidth;
                uint height = decoder.PixelHeight;

                double scale = 1.0;
                if (maxWidth > 0 && width > maxWidth)
                {
                    scale = Math.Min(scale, (double)maxWidth / width);
                }

                if (maxHeight > 0 && height > maxHeight)
                {
                    scale = Math.Min(scale, (double)maxHeight / height);
                }

                if (scale < 1.0)
                {
                    uint newWidth = (uint)Math.Round(width * scale);
                    uint newHeight = (uint)Math.Round(height * scale);

                    var transform = new BitmapTransform
                    {
                        ScaledWidth = newWidth,
                        ScaledHeight = newHeight,
                        InterpolationMode = BitmapInterpolationMode.Fant
                    };

                    var pixelData = await decoder.GetPixelDataAsync(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Premultiplied,
                        transform,
                        ExifOrientationMode.IgnoreExifOrientation,
                        ColorManagementMode.DoNotColorManage);

                    var pixels = pixelData.DetachPixelData();
                    bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, (int)newWidth, (int)newHeight, BitmapAlphaMode.Premultiplied);
                    bitmap.CopyFromBuffer(pixels.AsBuffer());

                    // Encode to PNG stream for OOP
                    scaledStream = new InMemoryRandomAccessStream();
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, scaledStream);
                    encoder.SetSoftwareBitmap(bitmap);
                    await encoder.FlushAsync();
                    scaledStream.Seek(0);

                    bitmap.Dispose();
                    stream.Dispose(); // now safe to dispose!
                    return new ThumbnailInfo(hash, scaledStream);
                }
            }

            // If not scaling, just re-use the original buffer in a new stream
            scaledStream = new InMemoryRandomAccessStream();
            await scaledStream.WriteAsync(buffer.AsBuffer());
            scaledStream.Seek(0);

            stream.Dispose(); // now safe to dispose!
            return new ThumbnailInfo(hash, scaledStream);
        }
        catch
        {
            // Dispose everything on error
            scaledStream?.Dispose();
            bitmap?.Dispose();
            stream?.Dispose();
            throw;
        }
    }


    private static string ComputeHash(byte[] data)
    {
        return Convert.ToHexString(SHA256.HashData(data));
    }
}
