// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Media.Vlc;

/// <summary>Immutable connection settings for one VLC HTTP interface.</summary>
/// <param name="Port">Legacy loopback port used when Endpoint is null, from 1 through 65535.</param>
/// <param name="Password">HTTP password with a blank username; an empty password prevents connection.</param>
public sealed record VlcConnectionOptions(int Port = 8080, string Password = "")
{
    private readonly bool? _treatAsLocal;

    /// <summary>Gets an absolute HTTP or HTTPS server URL; null uses the legacy loopback port.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Gets whether sessions participate in local grouping and automatic playback behavior.</summary>
    /// <remarks>Defaults to loopback detection. An explicit value overrides behavior, never native source ownership.</remarks>
    public bool TreatAsLocal { get => this._treatAsLocal ?? this.IsLocalConnection; init => this._treatAsLocal = value; }

    /// <summary>Gets whether the validated endpoint explicitly addresses this computer through loopback.</summary>
    /// <remarks>Hostnames and LAN addresses are conservatively remote; no DNS lookup is used for source ownership.</remarks>
    public bool IsLocalConnection => this.BaseAddress?.IsLoopback == true;

    /// <summary>Gets the normalized server root, or null when the endpoint is invalid.</summary>
    public Uri? BaseAddress => ParseEndpoint(this.Endpoint ?? $"http://127.0.0.1:{this.Port}/");

    /// <summary>Validates an HTTP(S) server root without embedded credentials, a path, query, or fragment.</summary>
    /// <param name="endpoint">User-supplied server URL.</param>
    /// <returns>A normalized server root, or null when invalid. This does not perform network I/O.</returns>
    public static Uri? ParseEndpoint(string? endpoint)
    {
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) || uri.Port is < 1 or > 65535 ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            return null;
        }

        return new(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    /// <summary>Returns connection details without exposing credentials.</summary>
    public override string ToString() => this.BaseAddress is { } endpoint ? $"VLC at {endpoint}" : "VLC (invalid server URL)";
}