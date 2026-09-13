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

    public void Load(Settings settings)
    {
        lock (this._gate)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    settings.Update(File.ReadAllText(filePath));
                }
            }
            catch (Exception ex)
            {
                LoadFailed(logger, ex);
            }
        }
    }

    public void Save(Settings settings)
    {
        lock (this._gate)
        {
            var saved = File.Exists(filePath)
                ? JsonNode.Parse(File.ReadAllText(filePath)) as JsonObject ?? throw new InvalidDataException("Invalid settings file.")
                : new JsonObject();
            var changed = JsonNode.Parse(settings.ToJson())!.AsObject();
            foreach (var entry in changed)
            {
                saved[entry.Key] = entry.Value?.DeepClone();
            }

            // All forms share this writer and merge only the keys they own.
            var temporaryPath = filePath + ".tmp";
            File.WriteAllText(temporaryPath, saved.ToJsonString(JsonOptions));
            File.Move(temporaryPath, filePath, overwrite: true);
        }
    }
}