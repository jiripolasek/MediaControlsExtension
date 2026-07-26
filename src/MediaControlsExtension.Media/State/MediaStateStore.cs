// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.State;

internal sealed class MediaStateStore
{
    private readonly Lock _stateLock = new();
    private readonly Dictionary<MediaSessionId, PendingPlaybackState> _pendingPlayback = [];
    private MediaServiceOptions _options = MediaServiceOptions.Default;
    private MediaServiceSnapshot _current = MediaServiceSnapshot.Initial;

    public MediaServiceSnapshot Current
    {
        get
        {
            lock (this._stateLock)
            {
                return this._current;
            }
        }
    }

    public void UpdateOptions(MediaServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (this._stateLock)
        {
            this._options = options;
        }
    }

    public MediaServiceSnapshot SetStatus(
        MediaServiceStatus status,
        MediaControlAvailability availability)
    {
        lock (this._stateLock)
        {
            return this.SetCurrentUnderLock(this._current with
            {
                Revision = this._current.Revision + 1,
                Status = status,
                Availability = availability,
            });
        }
    }

    public MediaServiceSnapshot ApplyBackendSnapshot(MediaBackendSnapshot backendSnapshot)
    {
        ArgumentNullException.ThrowIfNull(backendSnapshot);

        lock (this._stateLock)
        {
            var liveSessionIds = backendSnapshot.Sessions
                .Select(static session => new MediaSessionId(session.Id.Value))
                .ToHashSet();
            foreach (var removedId in this._pendingPlayback.Keys.Where(id => !liveSessionIds.Contains(id)).ToArray())
            {
                this._pendingPlayback.Remove(removedId);
            }

            var sessions = ImmutableArray.CreateBuilder<MediaSessionSnapshot>(backendSnapshot.Sessions.Length);
            foreach (var backendSession in backendSnapshot.Sessions)
            {
                var sessionId = new MediaSessionId(backendSession.Id.Value);
                var confirmedState = backendSession.PlaybackState;
                var effectiveState = confirmedState;
                var isOptimistic = false;

                if (this._pendingPlayback.TryGetValue(sessionId, out var pending))
                {
                    if (pending.BindingGeneration != backendSession.BindingGeneration ||
                        pending.PredictedState == confirmedState)
                    {
                        this._pendingPlayback.Remove(sessionId);
                    }
                    else
                    {
                        effectiveState = pending.PredictedState;
                        isOptimistic = true;
                    }
                }

                sessions.Add(new(
                    sessionId,
                    backendSession.BindingGeneration,
                    backendSession.MediaProperties,
                    backendSession.TimelineProperties,
                    new(
                        confirmedState,
                        effectiveState,
                        isOptimistic,
                        backendSession.Capabilities,
                        ResolvePrimaryOperation(effectiveState, backendSession.Capabilities))));
            }

            MediaSessionId? currentSessionId = backendSnapshot.CurrentSessionId is { } currentBackendId &&
                                               liveSessionIds.Contains(new(currentBackendId.Value))
                ? new MediaSessionId(currentBackendId.Value)
                : null;
            var status = backendSnapshot.Availability == MediaControlAvailability.Available
                ? MediaServiceStatus.Ready
                : MediaServiceStatus.Degraded;
            return this.SetCurrentUnderLock(new(
                this._current.Revision + 1,
                status,
                sessions.MoveToImmutable(),
                currentSessionId,
                backendSnapshot.Availability));
        }
    }

    public MediaCommandSubmissionStatus TryResolveCommand(
        MediaCommand command,
        out ResolvedMediaCommand resolvedCommand)
    {
        lock (this._stateLock)
        {
            resolvedCommand = default;
            if (this._current.Status is MediaServiceStatus.Stopped or MediaServiceStatus.Starting)
            {
                return MediaCommandSubmissionStatus.NotReady;
            }

            if (this._current.Status == MediaServiceStatus.Faulted ||
                this._current.Availability is MediaControlAvailability.Unavailable or MediaControlAvailability.CircuitOpen)
            {
                return MediaCommandSubmissionStatus.Unavailable;
            }

            if (this._current.Availability == MediaControlAvailability.Busy)
            {
                return MediaCommandSubmissionStatus.Busy;
            }

            var targetId = command.Target.Kind switch
            {
                MediaCommandTargetKind.CurrentSession => this._current.CurrentSessionId,
                MediaCommandTargetKind.Session => command.Target.SessionId,
                _ => null,
            };
            if (targetId is null)
            {
                return MediaCommandSubmissionStatus.SessionGone;
            }

            var targetIndex = FindSessionIndex(this._current.Sessions, targetId.Value);
            if (targetIndex < 0)
            {
                return MediaCommandSubmissionStatus.SessionGone;
            }

            var operation = command.Operation;
            if (operation is MediaOperation.SwitchNextSession or MediaOperation.SwitchPreviousSession)
            {
                if (this._current.Sessions.Length <= 1)
                {
                    return MediaCommandSubmissionStatus.Unsupported;
                }

                var offset = operation == MediaOperation.SwitchNextSession ? 1 : -1;
                targetIndex = (targetIndex + this._current.Sessions.Length + offset) % this._current.Sessions.Length;
                operation = MediaOperation.Play;
            }

            var target = this._current.Sessions[targetIndex];
            if (operation == MediaOperation.TogglePlayback)
            {
                operation = target.PlaybackInfo.PrimaryOperation;
            }

            if (!IsSupported(operation, target.PlaybackInfo.Capabilities))
            {
                return MediaCommandSubmissionStatus.Unsupported;
            }

            var sessionsToPause = this._options.PauseOtherSessionsOnPlay && operation == MediaOperation.Play
                ? this._current.Sessions
                    .Where(session => session.Id != target.Id)
                    .Select(static session => new MediaBackendSessionId(session.Id.Value))
                    .ToImmutableArray()
                : [];
            resolvedCommand = new(
                command.Operation,
                operation,
                target.Id,
                new MediaBackendSessionId(target.Id.Value),
                target.BindingGeneration,
                sessionsToPause);
            return MediaCommandSubmissionStatus.Accepted;
        }
    }

    public MediaServiceSnapshot ApplyPrediction(
        ResolvedMediaCommand command,
        MediaOperationId operationId)
    {
        lock (this._stateLock)
        {
            var predictedState = command.ResolvedOperation switch
            {
                MediaOperation.Play => MediaPlaybackState.Playing,
                MediaOperation.Pause => MediaPlaybackState.Paused,
                MediaOperation.Stop => MediaPlaybackState.Stopped,
                _ => (MediaPlaybackState?)null,
            };
            if (predictedState is null)
            {
                return this._current;
            }

            var index = FindSessionIndex(this._current.Sessions, command.SessionId);
            if (index < 0)
            {
                return this._current;
            }

            var session = this._current.Sessions[index];
            this._pendingPlayback[command.SessionId] = new(
                operationId,
                command.BindingGeneration,
                predictedState.Value);
            var updatedSession = session with
            {
                PlaybackInfo = session.PlaybackInfo with
                {
                    EffectiveState = predictedState.Value,
                    IsOptimistic = true,
                    PrimaryOperation = ResolvePrimaryOperation(
                        predictedState.Value,
                        session.PlaybackInfo.Capabilities),
                },
            };
            return this.ReplaceSessionUnderLock(index, updatedSession);
        }
    }

    public MediaServiceSnapshot CompleteCommand(
        ResolvedMediaCommand command,
        MediaOperationId operationId,
        bool succeeded)
    {
        lock (this._stateLock)
        {
            if (succeeded ||
                !this._pendingPlayback.TryGetValue(command.SessionId, out var pending) ||
                pending.OperationId != operationId)
            {
                return this._current;
            }

            this._pendingPlayback.Remove(command.SessionId);
            var index = FindSessionIndex(this._current.Sessions, command.SessionId);
            if (index < 0)
            {
                return this._current;
            }

            var session = this._current.Sessions[index];
            var updatedSession = session with
            {
                PlaybackInfo = session.PlaybackInfo with
                {
                    EffectiveState = session.PlaybackInfo.ConfirmedState,
                    IsOptimistic = false,
                    PrimaryOperation = ResolvePrimaryOperation(
                        session.PlaybackInfo.ConfirmedState,
                        session.PlaybackInfo.Capabilities),
                },
            };
            return this.ReplaceSessionUnderLock(index, updatedSession);
        }
    }

    public bool TryExpirePrediction(
        MediaSessionId sessionId,
        MediaOperationId operationId,
        out MediaServiceSnapshot snapshot)
    {
        lock (this._stateLock)
        {
            snapshot = this._current;
            if (!this._pendingPlayback.TryGetValue(sessionId, out var pending) ||
                pending.OperationId != operationId)
            {
                return false;
            }

            this._pendingPlayback.Remove(sessionId);
            var index = FindSessionIndex(this._current.Sessions, sessionId);
            if (index < 0)
            {
                return false;
            }

            var session = this._current.Sessions[index];
            var updatedSession = session with
            {
                PlaybackInfo = session.PlaybackInfo with
                {
                    EffectiveState = session.PlaybackInfo.ConfirmedState,
                    IsOptimistic = false,
                    PrimaryOperation = ResolvePrimaryOperation(
                        session.PlaybackInfo.ConfirmedState,
                        session.PlaybackInfo.Capabilities),
                },
            };
            snapshot = this.ReplaceSessionUnderLock(index, updatedSession);
            return true;
        }
    }

    private static int FindSessionIndex(
        ImmutableArray<MediaSessionSnapshot> sessions,
        MediaSessionId sessionId)
    {
        for (var index = 0; index < sessions.Length; index++)
        {
            if (sessions[index].Id == sessionId)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsSupported(MediaOperation operation, MediaCapabilities capabilities)
    {
        var requiredCapability = operation switch
        {
            MediaOperation.Play => MediaCapabilities.Play,
            MediaOperation.Pause => MediaCapabilities.Pause,
            MediaOperation.Stop => MediaCapabilities.Stop,
            MediaOperation.SkipNext => MediaCapabilities.SkipNext,
            MediaOperation.SkipPrevious => MediaCapabilities.SkipPrevious,
            MediaOperation.ToggleShuffle => MediaCapabilities.ToggleShuffle,
            MediaOperation.ToggleRepeat => MediaCapabilities.ToggleRepeat,
            _ => MediaCapabilities.None,
        };
        return requiredCapability != MediaCapabilities.None &&
               capabilities.HasFlag(requiredCapability);
    }

    private static MediaOperation ResolvePrimaryOperation(
        MediaPlaybackState playbackState,
        MediaCapabilities capabilities)
    {
        if (playbackState != MediaPlaybackState.Playing)
        {
            return MediaOperation.Play;
        }

        if (capabilities.HasFlag(MediaCapabilities.Pause))
        {
            return MediaOperation.Pause;
        }

        return capabilities.HasFlag(MediaCapabilities.Stop)
            ? MediaOperation.Stop
            : MediaOperation.Pause;
    }

    private MediaServiceSnapshot ReplaceSessionUnderLock(
        int index,
        MediaSessionSnapshot updatedSession)
    {
        var sessions = this._current.Sessions.SetItem(index, updatedSession);
        return this.SetCurrentUnderLock(this._current with
        {
            Revision = this._current.Revision + 1,
            Sessions = sessions,
        });
    }

    private MediaServiceSnapshot SetCurrentUnderLock(MediaServiceSnapshot snapshot)
    {
        this._current = snapshot;
        return snapshot;
    }

    private sealed record PendingPlaybackState(
        MediaOperationId OperationId,
        long BindingGeneration,
        MediaPlaybackState PredictedState);
}

internal readonly record struct ResolvedMediaCommand(
    MediaOperation RequestedOperation,
    MediaOperation ResolvedOperation,
    MediaSessionId SessionId,
    MediaBackendSessionId BackendSessionId,
    long BindingGeneration,
    ImmutableArray<MediaBackendSessionId> SessionsToPause);