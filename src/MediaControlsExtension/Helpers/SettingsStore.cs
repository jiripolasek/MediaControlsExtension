// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Microsoft.Extensions.Logging;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal sealed class SettingsStore(string filePath, ILogger logger)
{
    private static readonly Action<ILogger, Exception?> LoadFailed = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1, nameof(LoadFailed)), "Could not load extension settings.");
    private readonly Lock _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public SettingsReadResult ReadValues(bool logFailures = true)
    {
        lock (this._gate) { return this.ReadValuesUnderLock(logFailures); }
    }

    private SettingsReadResult ReadValuesUnderLock(bool logFailures = true)
    {
        string text;
        try
        {
            text = File.ReadAllText(filePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(SettingsReadStatus.Missing, new JsonObject());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (logFailures) { LoadFailed(logger, ex); }
            return new(SettingsReadStatus.Unreadable, null, ex);
        }

        try
        {
            var saved = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("Invalid settings file.");
            ValidateProperties(saved);
            return new(SettingsReadStatus.Loaded, saved);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
        {
            if (logFailures) { LoadFailed(logger, ex); }
            return new(SettingsReadStatus.Malformed, new JsonObject(), ex);
        }
    }

    private static void ValidateProperties(JsonNode? node)
    {
        // Materialize lazy objects so duplicate keys fail during both load and save.
        if (node is JsonObject properties)
        {
            foreach (var property in properties) { ValidateProperties(property.Value); }
        }
        else if (node is JsonArray items)
        {
            foreach (var item in items) { ValidateProperties(item); }
        }
    }

    public void Load(Settings settings)
    {
        try
        {
            var saved = this.ReadValues();
            if (saved.Status == SettingsReadStatus.Loaded)
            {
                settings.Update(saved.Values!.ToJsonString());
            }
        }
        catch (Exception ex) { LoadFailed(logger, ex); }
    }

    public void Save(Settings settings)
    {
        lock (this._gate)
        {
            var current = this.ReadValuesUnderLock();
            if (current.Status == SettingsReadStatus.Unreadable)
            {
                throw new IOException("Cannot save settings while the existing file is unreadable.", current.Error);
            }
            var saved = current.Values!;
            var changed = JsonNode.Parse(settings.ToJson())!.AsObject();
            foreach (var entry in changed)
            {
                saved[entry.Key] = entry.Value?.DeepClone();
            }

            // All forms share this writer and merge only the keys they own.
            var temporaryPath = filePath + ".tmp";
            File.WriteAllText(temporaryPath, saved.ToJsonString(JsonOptions));
            if (current.Status == SettingsReadStatus.Malformed) { File.Copy(filePath, filePath + $".invalid-{Guid.NewGuid():N}"); }
            File.Move(temporaryPath, filePath, overwrite: true);
        }
    }
}

internal enum SettingsReadStatus { Missing, Loaded, Malformed, Unreadable }

internal sealed record SettingsReadResult(SettingsReadStatus Status, JsonObject? Values, Exception? Error = null);