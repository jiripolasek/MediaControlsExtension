// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Text.Json.Nodes;
using JPSoftworks.MediaControlsExtension.Media.Vlc;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal sealed class VlcEndpointSetting : TextSetting
{
    internal const string EndpointKey = "jpsoftworks.mediacontrols.MediaBackends.vlc.Endpoint";
    private const string LegacyPortKey = "jpsoftworks.mediacontrols.MediaBackends.vlc.Port";
    internal const string DefaultEndpoint = "http://127.0.0.1:8080/";

    public VlcEndpointSetting() : base(EndpointKey, Resource("Settings_Vlc_Endpoint_Title"),
        Resource("Settings_Vlc_Endpoint_Description"), DefaultEndpoint)
    {
        this.Placeholder = DefaultEndpoint;
    }

    public string? LegacyCredentialEndpoint { get; private set; } = DefaultEndpoint;

    public string ReadValue(JsonObject payload)
    {
        if (payload[this.Key] is JsonValue value && value.TryGetValue<string>(out var endpoint)) return endpoint;
        if (payload[LegacyPortKey] is JsonValue port && port.TryGetValue<string>(out var text)) return $"http://127.0.0.1:{text}/";
        return this.Value ?? DefaultEndpoint;
    }

    public override void Update(JsonObject payload)
    {
        var endpoint = this.ReadValue(payload);
        this.Value = VlcConnectionOptions.ParseEndpoint(endpoint)?.AbsoluteUri ?? endpoint;
        if (!payload.ContainsKey(this.Key) && payload.ContainsKey(LegacyPortKey))
            this.LegacyCredentialEndpoint = VlcConnectionOptions.ParseEndpoint(endpoint)?.AbsoluteUri;
    }

    private static string Resource(string key) => Strings.ResourceManager.GetString(key, Strings.Culture)!;
}

internal sealed class VlcTreatmentSetting : Setting<string>
{
    private const string LegacyKey = "jpsoftworks.mediacontrols.MediaBackends.vlc.TreatAsLocal";

    public VlcTreatmentSetting() : base("jpsoftworks.mediacontrols.MediaBackends.vlc.LocalTreatment",
        Resource("Settings_Vlc_TreatAsLocal_Title"), Resource("Settings_Vlc_TreatAsLocal_Description"), "auto")
    {
    }

    public override Dictionary<string, object> ToDictionary() => new()
    {
        ["type"] = "Input.ChoiceSet", ["id"] = this.Key, ["title"] = this.Label,
        ["label"] = this.Description, ["value"] = this.Value ?? "auto", ["style"] = "compact",
        ["choices"] = new List<ChoiceSetSetting.Choice>
        {
            new(Resource("Settings_Vlc_Treatment_Auto"), "auto"),
            new(Resource("Settings_Vlc_Treatment_Local"), "local"),
            new(Resource("Settings_Vlc_Treatment_Remote"), "remote"),
        },
    };

    public override string ToState() => $"\"{this.Key}\": {JsonValue.Create(this.Value ?? "auto")!.ToJsonString()}";

    public override void Update(JsonObject payload)
    {
        if (payload[this.Key] is JsonValue value && value.TryGetValue<string>(out var treatment) &&
            treatment is "auto" or "local" or "remote") this.Value = treatment;
        else if (!payload.ContainsKey(this.Key) && payload[LegacyKey] is JsonValue legacy && legacy.TryGetValue<bool>(out var local))
            this.Value = local ? "auto" : "remote";
    }

    private static string Resource(string key) => Strings.ResourceManager.GetString(key, Strings.Culture)!;
}