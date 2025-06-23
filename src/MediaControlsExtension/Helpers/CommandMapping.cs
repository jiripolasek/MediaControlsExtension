// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Diagnostics.CodeAnalysis;

namespace JPSoftworks.MediaControlsExtension.Helpers;

[method:SetsRequiredMembers]
internal sealed record CommandMapping(string Prefix, string CommandName);