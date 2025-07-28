// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal abstract class MediaSessionOp
{
    public virtual bool CanExecute(GlobalSystemMediaTransportControlsSessionManager manager, GlobalSystemMediaTransportControlsSession session) => true;

    public abstract Task<MediaSessionOperationResult> InvokeAsync(GlobalSystemMediaTransportControlsSessionManager manager, GlobalSystemMediaTransportControlsSession session);
}