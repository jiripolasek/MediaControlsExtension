// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace JPSoftworks.MediaControlsExtension.Media.Browser;

internal static class BrowserWire
{
    internal const int MaximumBytes = 900_000;

    internal static string DefaultPipeName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("No user SID.");
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..16];
        return $"JPSoftworks.BrowserMedia.Spike.{suffix}";
    }

    internal static NamedPipeClientStream CreatePipe(string name) => new(
        ".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    internal static async Task WriteAsync(Stream stream, Action<Utf8JsonWriter> write, CancellationToken cancellationToken)
    {
        using var payload = new MemoryStream();
        using (var writer = new Utf8JsonWriter(payload))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        if (payload.Length > MaximumBytes) throw new InvalidDataException("Browser message too large.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, (int)payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload.GetBuffer().AsMemory(0, (int)payload.Length), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<JsonDocument> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumBytes) throw new InvalidDataException("Invalid browser message size.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 24 });
    }

    internal static string Text(JsonElement value, string name, bool required = false)
    {
        var text = value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()! : string.Empty;
        if (text.Length > 4096 || (required && string.IsNullOrWhiteSpace(text)))
            throw new InvalidDataException($"Invalid browser field: {name}.");
        return text;
    }
}