// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Text.Json.Nodes;

namespace JPSoftworks.MediaControlsExtension.Helpers;

/// <summary>
/// A presentation-only settings element that renders an Adaptive Card group header.
/// </summary>
internal sealed class SettingsGroupHeader : Setting<string>
{
    private readonly bool _showSeparator;

    public SettingsGroupHeader(string key, string title, bool showSeparator = true)
        : base(key, title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        this._showSeparator = showSeparator;
    }

    public override Dictionary<string, object> ToDictionary()
    {
        return new()
        {
            { "type", "TextBlock" },
            { "text", this.Value ?? string.Empty },
            { "weight", "Bolder" },
            { "size", "Medium" },
            { "wrap", true },
            { "separator", this._showSeparator },
            { "spacing", this._showSeparator ? "Large" : "None" },
        };
    }

    public override void Update(JsonObject payload)
    {
        // This element is presentation-only and ignores submitted form data.
    }

    public override string ToState()
    {
        // Settings requires every form element to contribute a JSON property.
        // A stable null entry keeps the pseudo-setting inert without accumulating
        // random keys in settings.json across extension launches.
        return $"\"{this.Key}\": null";
    }
}
