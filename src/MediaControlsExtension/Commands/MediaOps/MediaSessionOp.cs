// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal abstract class MediaSessionOp
{
    public virtual bool CanExecute(MediaSource source) => true;

    public Task<MediaSessionOperationResult> InvokeAsync(
        GlobalSystemMediaTransportControlsSessionManager manager,
        GlobalSystemMediaTransportControlsSession session)
    {
        GsmtcOperationGate.VerifyAccess();
        return this.InvokeUnderGateAsync(manager, session);
    }

    protected abstract Task<MediaSessionOperationResult> InvokeUnderGateAsync(
        GlobalSystemMediaTransportControlsSessionManager manager,
        GlobalSystemMediaTransportControlsSession session);
}