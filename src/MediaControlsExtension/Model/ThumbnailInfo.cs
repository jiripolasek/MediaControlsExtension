// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Storage.Streams;

namespace JPSoftworks.MediaControlsExtension.Model;

internal sealed record ThumbnailInfo(string? Hash, IRandomAccessStream? Stream);