// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;

namespace JPSoftworks.MediaControlsExtension.Media.State;

internal sealed class MediaSessionCatalog
{
    private readonly Lock _stateLock = new();
    private readonly Dictionary<MediaSessionId, MediaSession> _sessions = [];
    private MediaServiceState _state = MediaServiceState.Initial;
    private long _lastSnapshotRevision = -1;

    public MediaServiceState State => Volatile.Read(ref this._state);

    public MediaStatePublication Apply(MediaServiceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (this._stateLock)
        {
            if (snapshot.Revision <= this._lastSnapshotRevision)
            {
                return MediaStatePublication.Empty;
            }

            this._lastSnapshotRevision = snapshot.Revision;
            var previousState = this._state;
            var sessionNotifications = ImmutableArray.CreateBuilder<MediaSessionNotification>();
            var sourceSessions = snapshot.Status == MediaServiceStatus.Stopped
                ? []
                : snapshot.Sessions;
            var liveSessionIds = sourceSessions
                .Select(static session => session.Id)
                .ToHashSet();
            var sessions = ImmutableArray.CreateBuilder<MediaSession>(sourceSessions.Length);

            foreach (var sessionSnapshot in sourceSessions)
            {
                if (!this._sessions.TryGetValue(sessionSnapshot.Id, out var session))
                {
                    session = new(sessionSnapshot);
                    this._sessions.Add(sessionSnapshot.Id, session);
                }
                else
                {
                    var changes = session.Apply(sessionSnapshot);
                    if (changes != MediaSessionChanges.None)
                    {
                        sessionNotifications.Add(new(session, changes));
                    }
                }

                sessions.Add(session);
            }

            foreach (var removedId in this._sessions.Keys.Where(id => !liveSessionIds.Contains(id)).ToArray())
            {
                var removed = this._sessions[removedId];
                this._sessions.Remove(removedId);
                var changes = removed.MarkUnavailable();
                if (changes != MediaSessionChanges.None)
                {
                    sessionNotifications.Add(new(removed, changes));
                }
            }

            var currentSession = snapshot.CurrentSessionId is { } currentId
                ? this._sessions.GetValueOrDefault(currentId)
                : null;
            var serviceChanges = MediaServiceChanges.None;
            if (snapshot.Status != previousState.Status)
            {
                serviceChanges |= MediaServiceChanges.Status;
            }

            if (snapshot.Availability != previousState.Availability)
            {
                serviceChanges |= MediaServiceChanges.Availability;
            }

            var publishedSessions = sessions.MoveToImmutable();
            if (!SessionsEqual(previousState.Sessions, publishedSessions))
            {
                serviceChanges |= MediaServiceChanges.Sessions;
            }

            if (!ReferenceEquals(previousState.CurrentSession, currentSession))
            {
                serviceChanges |= MediaServiceChanges.CurrentSession;
            }

            if (serviceChanges != MediaServiceChanges.None)
            {
                Volatile.Write(
                    ref this._state,
                    new(
                        previousState.Revision + 1,
                        snapshot.Status,
                        snapshot.Availability,
                        publishedSessions,
                        currentSession));
            }

            return new(sessionNotifications.ToImmutable(), serviceChanges);
        }
    }

    private static bool SessionsEqual(
        ImmutableArray<MediaSession> left,
        ImmutableArray<MediaSession> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Length; index++)
        {
            if (!ReferenceEquals(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }
}

internal readonly record struct MediaSessionNotification(
    MediaSession Session,
    MediaSessionChanges Changes);

internal sealed record MediaStatePublication(
    ImmutableArray<MediaSessionNotification> SessionNotifications,
    MediaServiceChanges ServiceChanges)
{
    public static MediaStatePublication Empty { get; } = new([], MediaServiceChanges.None);
}