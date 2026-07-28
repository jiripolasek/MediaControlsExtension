// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Pages;

internal sealed record DockHeadCommandTargets(
    IPage MediaControlsPage,
    IPage? CurrentMediaMetadataPage);
