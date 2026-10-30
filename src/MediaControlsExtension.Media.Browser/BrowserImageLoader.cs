// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace JPSoftworks.MediaControlsExtension.Media.Browser;

internal sealed class BrowserImageLoader : IDisposable
{
    internal const int MaximumBytes = 2 * 1024 * 1024;
    private readonly HttpClient _client;
    private readonly string _iconDirectory;
    private readonly HashSet<string> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _readyFiles = new(StringComparer.Ordinal);

    internal BrowserImageLoader(HttpMessageHandler? handler = null, string? cacheDirectory = null)
    {
        this._client = new(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false,
            AutomaticDecompression = DecompressionMethods.None
        });
        this._client.Timeout = Timeout.InfiniteTimeSpan;
        this._iconDirectory = Path.Combine(cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JPSoftworks", "MediaControlsExtension", "BrowserIcons"),
            Guid.NewGuid().ToString("N"));
    }

    internal static string ImageUrl(string value)
    {
        return value.Length <= 2048 && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo)
            ? uri.AbsoluteUri : string.Empty;
    }

    internal async Task<MediaArtworkContent?> LoadAsync(string url, bool icon, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            for (var redirect = 0; redirect <= 3 && ImageUrl(url).Length > 0; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await this._client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and <= 399 && response.Headers.Location is { } location)
                {
                    url = new Uri(new Uri(url), location).AbsoluteUri;
                    continue;
                }

                var limit = icon ? 256 * 1024 : MaximumBytes;
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > limit) return null;
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var output = new MemoryStream();
                var buffer = new byte[8192];
                while (true)
                {
                    var count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    if (output.Length + count > limit) return null;
                    output.Write(buffer, 0, count);
                }

                var data = output.ToArray();
                var type = ContentType(data, icon);
                return type is null ? null : new(type, data, Convert.ToHexString(SHA256.HashData(data)));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or IOException or UriFormatException) { }
        return null;
    }

    // Use the encoded signature, not the server's claimed content type.
    private static string? ContentType(ReadOnlySpan<byte> data, bool icon)
    {
        if (data.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (data.StartsWith(new byte[] { 255, 216, 255 })) return "image/jpeg";
        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)) return "image/gif";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        if (IsAvif(data)) return "image/avif";
        if (icon && data.Length >= 6 && data[..4].SequenceEqual(new byte[] { 0, 0, 1, 0 })) return "image/x-icon";
        return null;
    }

    private static bool IsAvif(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16 || !data.Slice(4, 4).SequenceEqual("ftyp"u8)) return false;
        var size = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (size < 16 || size > data.Length || size % 4 != 0) return false;
        for (var offset = 8; offset < size; offset += 4)
        {
            if (offset == 12) continue; // The minor version is not a brand.
            var brand = data.Slice(offset, 4);
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8)) return true;
        }
        return false;
    }

    internal async Task<string?> LoadIconAsync(string url, CancellationToken cancellationToken)
    {
        var content = await this.LoadAsync(url, icon: true, cancellationToken).ConfigureAwait(false);
        if (content is null) return null;
        var extension = content.ContentType switch
        {
            "image/png" => ".png", "image/jpeg" => ".jpg", "image/gif" => ".gif",
            "image/webp" => ".webp", "image/avif" => ".avif", _ => ".ico"
        };
        var path = Path.Combine(this._iconDirectory, content.Hash + extension);
        if (this._readyFiles.Contains(path)) return path;
        if (this._files.Count >= 128) return null;
        try
        {
            Directory.CreateDirectory(this._iconDirectory);
            this._files.Add(path);
            await File.WriteAllBytesAsync(path, content.Data.ToArray(), cancellationToken).ConfigureAwait(false);
            this._readyFiles.Add(path);
            return path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Dispose()
    {
        this._client.Dispose();
        foreach (var path in this._files)
        {
            try { File.Delete(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        try { Directory.Delete(this._iconDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}