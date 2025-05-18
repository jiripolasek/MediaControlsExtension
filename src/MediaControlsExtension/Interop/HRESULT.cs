// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Diagnostics.CodeAnalysis;

namespace JPSoftworks.MediaControlsExtension.Interop;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Native")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public enum HRESULT : uint
{
    ERROR_NOT_FOUND = 0x80070490,
}