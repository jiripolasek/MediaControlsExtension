using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BrowserCompanion;

internal static class Wire
{
    internal const int MaximumBytes = 900_000;

    internal static async Task<JsonObject?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        if (await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken) == 0)
        {
            return null;
        }

        await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumBytes)
        {
            throw new InvalidDataException("Invalid message size.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonNode.Parse(payload, documentOptions: new JsonDocumentOptions { MaxDepth = 24 }) as JsonObject
            ?? throw new InvalidDataException("Expected a JSON object.");
    }

    internal static async Task WriteAsync(Stream stream, JsonObject message, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) message.WriteTo(writer);
        var payload = buffer.ToArray();
        if (payload.Length > MaximumBytes)
        {
            throw new InvalidDataException("Message too large.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    internal static string Text(JsonObject value, string key) => value[key]?.GetValue<string>() ?? string.Empty;

    internal static JsonObject Status(string status) => new() { ["status"] = status };
}