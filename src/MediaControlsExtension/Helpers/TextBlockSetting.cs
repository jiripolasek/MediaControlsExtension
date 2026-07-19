// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Text.Json.Nodes;

namespace JPSoftworks.MediaControlsExtension.Helpers;

/// <summary>
/// A presentation-only settings element that renders formatted Adaptive Card text.
/// </summary>
internal sealed class TextBlockSetting : Setting<string>
{
    private readonly bool _isSubtle;

    public TextBlockSetting(string key, string text, bool isSubtle = false)
        : base(key, text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        this._isSubtle = isSubtle;
    }

    public override Dictionary<string, object> ToDictionary()
    {
        return new()
        {
            { "type", "TextBlock" },
            { "text", this.Value ?? string.Empty },
            { "isSubtle", this._isSubtle },
            { "wrap", true },
        };
    }

    public override void Update(JsonObject payload)
    {
        // This element is presentation-only and ignores submitted form data.
    }

    public override string ToState()
    {
        // Settings requires every form element to contribute a JSON property.
        return $"\"{this.Key}\": null";
    }
}
