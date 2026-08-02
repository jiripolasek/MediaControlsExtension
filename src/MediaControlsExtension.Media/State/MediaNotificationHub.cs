// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Threading.Channels;
using JPSoftworks.MediaControlsExtension.Media.Diagnostics;
using Microsoft.Extensions.Logging;

namespace JPSoftworks.MediaControlsExtension.Media.State;

internal sealed class MediaNotificationHub
{
    private readonly Action<MediaServiceChanges, Action<Exception>> _raiseServiceChanged;
    private readonly ILogger _logger;
    private readonly Lock _pendingLock = new();
    private readonly Dictionary<MediaSession, MediaSessionChanges> _pendingSessions = [];
    private readonly Channel<bool> _wake;
    private readonly Task _pumpTask;

    private MediaServiceChanges _pendingServiceChanges;

    public MediaNotificationHub(
        Action<MediaServiceChanges, Action<Exception>> raiseServiceChanged,
        ILogger logger)
    {
        this._raiseServiceChanged = raiseServiceChanged;
        this._logger = logger;
        this._wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false,
        });
        this._pumpTask = Task.Run(this.ProcessAsync);
    }

    public void Publish(MediaStatePublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        if (publication.SessionNotifications.IsEmpty &&
            publication.ServiceChanges == MediaServiceChanges.None)
        {
            return;
        }

        lock (this._pendingLock)
        {
            foreach (var notification in publication.SessionNotifications)
            {
                this._pendingSessions.TryGetValue(notification.Session, out var pending);
                this._pendingSessions[notification.Session] = pending | notification.Changes;
            }

            this._pendingServiceChanges |= publication.ServiceChanges;
        }

        this._wake.Writer.TryWrite(true);
    }

    public async Task CompleteAsync()
    {
        this._wake.Writer.TryComplete();
        await this._pumpTask.ConfigureAwait(false);
    }

    private async Task ProcessAsync()
    {
        await foreach (var _ in this._wake.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            while (this.TryTakePending(out var sessions, out var serviceChanges))
            {
                foreach (var (session, changes) in sessions)
                {
                    session.RaiseChanged(changes, this.ReportSubscriberException);
                }

                if (serviceChanges != MediaServiceChanges.None)
                {
                    this._raiseServiceChanged(serviceChanges, this.ReportSubscriberException);
                }
            }
        }

        while (this.TryTakePending(out var sessions, out var serviceChanges))
        {
            foreach (var (session, changes) in sessions)
            {
                session.RaiseChanged(changes, this.ReportSubscriberException);
            }

            if (serviceChanges != MediaServiceChanges.None)
            {
                this._raiseServiceChanged(serviceChanges, this.ReportSubscriberException);
            }
        }
    }

    private bool TryTakePending(
        out KeyValuePair<MediaSession, MediaSessionChanges>[] sessions,
        out MediaServiceChanges serviceChanges)
    {
        lock (this._pendingLock)
        {
            if (this._pendingSessions.Count == 0 &&
                this._pendingServiceChanges == MediaServiceChanges.None)
            {
                sessions = [];
                serviceChanges = MediaServiceChanges.None;
                return false;
            }

            sessions = [.. this._pendingSessions];
            serviceChanges = this._pendingServiceChanges;
            this._pendingSessions.Clear();
            this._pendingServiceChanges = MediaServiceChanges.None;
            return true;
        }
    }

    private void ReportSubscriberException(Exception exception)
    {
        MediaLog.StateSubscriberFailed(this._logger, exception);
    }
}