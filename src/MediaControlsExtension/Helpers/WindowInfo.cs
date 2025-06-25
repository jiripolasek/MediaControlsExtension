// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal sealed record WindowInfo
{
    public IntPtr Handle { get; set; }
    public string Title { get; set; } = "";
    public uint ProcessId { get; set; }
    public string? ProcessName { get; set; }
    public string? ClassName { get; set; }
    public string? MainModulePath { get; set; }
}