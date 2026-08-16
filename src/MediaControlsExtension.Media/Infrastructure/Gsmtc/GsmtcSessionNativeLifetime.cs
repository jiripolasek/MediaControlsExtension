// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Media.Infrastructure.Gsmtc;

/// <summary>
/// Keeps a GSMTC session's native state rooted while calls are active and
/// prevents retirement from unhooking the session until those calls finish.
/// </summary>
internal sealed class GsmtcSessionNativeLifetime
{
    private readonly record struct NativeObjectRoots(
        object? PlaybackInfo,
        object? PlaybackControls,
        object? CommandPlaybackInfo,
        object? CommandPlaybackControls,
        object? TimelineProperties,
        object? MediaProperties,
        object? Thumbnail,
        object? Genres);

    private readonly Lock _stateLock = new();
    private int _activeUseCount;
    private TaskCompletionSource? _activeUsesDrained;
    private NativeObjectRoots _retainedObjects;
    private Task<bool>? _retirementTask;
    private bool _isRetiring;

    internal int ActiveUseCount
    {
        get
        {
            lock (this._stateLock)
            {
                return this._activeUseCount;
            }
        }
    }

    internal bool IsRetiring
    {
        get
        {
            lock (this._stateLock)
            {
                return this._isRetiring;
            }
        }
    }

    internal object? RetainedPlaybackInfo
    {
        get
        {
            lock (this._stateLock)
            {
                return this._retainedObjects.PlaybackInfo;
            }
        }
    }

    internal object? RetainedPlaybackControls
    {
        get
        {
            lock (this._stateLock)
            {
                return this._retainedObjects.PlaybackControls;
            }
        }
    }

    internal object? RetainedTimelineProperties
    {
        get
        {
            lock (this._stateLock)
            {
                return this._retainedObjects.TimelineProperties;
            }
        }
    }

    internal object? RetainedCommandPlaybackInfo
    {
        get
        {
            lock (this._stateLock)
            {
                return this._retainedObjects.CommandPlaybackInfo;
            }
        }
    }

    internal object? RetainedCommandPlaybackControls
    {
        get
        {
            lock (this._stateLock)
            {
                return this._retainedObjects.CommandPlaybackControls;
            }
        }
    }

    internal object? RetainedMediaProperties
    {
        get
        {
            lock (this._stateLock)
            {
                return this._retainedObjects.MediaProperties;
            }
        }
    }

    internal object? RetainedThumbnail
    {
        get
        {
            lock (this._stateLock)
            {
                return this._retainedObjects.Thumbnail;
            }
        }
    }

    internal object? RetainedGenres
    {
        get
        {
            lock (this._stateLock)
            {
                return this._retainedObjects.Genres;
            }
        }
    }

    public NativeUse? TryEnter()
    {
        lock (this._stateLock)
        {
            if (this._isRetiring)
            {
                return null;
            }

            this._activeUseCount++;
            return new(this);
        }
    }

    public Task<bool> RetireAsync(Func<Task<bool>> retireNativeStateAsync)
    {
        ArgumentNullException.ThrowIfNull(retireNativeStateAsync);

        Task drainedTask;
        TaskCompletionSource<bool> retirementCompletion;
        lock (this._stateLock)
        {
            if (this._retirementTask is not null)
            {
                return this._retirementTask;
            }

            this._isRetiring = true;
            drainedTask = this._activeUseCount == 0
                ? Task.CompletedTask
                : (this._activeUsesDrained = new(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            retirementCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            this._retirementTask = retirementCompletion.Task;
        }

        _ = this.CompleteRetirementAsync(
            drainedTask,
            retireNativeStateAsync,
            retirementCompletion);
        return retirementCompletion.Task;
    }

    private async Task CompleteRetirementAsync(
        Task drainedTask,
        Func<Task<bool>> retireNativeStateAsync,
        TaskCompletionSource<bool> retirementCompletion)
    {
        Exception? retirementException = null;
        var retiredNativeState = false;
        try
        {
            await drainedTask.ConfigureAwait(false);
            retiredNativeState = await retireNativeStateAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            retirementException = ex;
        }

        if (retiredNativeState)
        {
            lock (this._stateLock)
            {
                this._retainedObjects = default;
            }
        }

        if (retirementException is null)
        {
            retirementCompletion.TrySetResult(retiredNativeState);
        }
        else
        {
            retirementCompletion.TrySetException(retirementException);
        }
    }

    private void CommitPlaybackObjects(
        object? playbackInfo,
        object? playbackControls)
    {
        lock (this._stateLock)
        {
            this._retainedObjects = this._retainedObjects with
            {
                PlaybackInfo = playbackInfo,
                PlaybackControls = playbackControls,
            };
        }
    }

    private void CommitTimelineObjects(object? timelineProperties)
    {
        lock (this._stateLock)
        {
            this._retainedObjects = this._retainedObjects with
            {
                TimelineProperties = timelineProperties,
            };
        }
    }

    private void CommitCommandPlaybackObjects(
        object? playbackInfo,
        object? playbackControls)
    {
        lock (this._stateLock)
        {
            this._retainedObjects = this._retainedObjects with
            {
                CommandPlaybackInfo = playbackInfo,
                CommandPlaybackControls = playbackControls,
            };
        }
    }

    private void CommitMediaObjects(
        object? mediaProperties,
        object? thumbnail,
        object? genres)
    {
        lock (this._stateLock)
        {
            this._retainedObjects = this._retainedObjects with
            {
                MediaProperties = mediaProperties,
                Thumbnail = thumbnail,
                Genres = genres,
            };
        }
    }

    private void Exit()
    {
        TaskCompletionSource? activeUsesDrained = null;
        lock (this._stateLock)
        {
            if (this._activeUseCount <= 0)
            {
                throw new InvalidOperationException("The GSMTC native-use count is already zero.");
            }

            this._activeUseCount--;
            if (this._activeUseCount == 0)
            {
                activeUsesDrained = this._activeUsesDrained;
                this._activeUsesDrained = null;
            }
        }

        activeUsesDrained?.TrySetResult();
    }

    internal sealed class NativeUse(GsmtcSessionNativeLifetime owner) : IDisposable
    {
        private GsmtcSessionNativeLifetime? _owner = owner;

        public void CommitPlaybackObjects(
            object? playbackInfo,
            object? playbackControls)
        {
            var currentOwner = Volatile.Read(ref this._owner)
                ?? throw new ObjectDisposedException(nameof(NativeUse));
            currentOwner.CommitPlaybackObjects(playbackInfo, playbackControls);
        }

        public void CommitTimelineObjects(object? timelineProperties)
        {
            var currentOwner = Volatile.Read(ref this._owner)
                ?? throw new ObjectDisposedException(nameof(NativeUse));
            currentOwner.CommitTimelineObjects(timelineProperties);
        }

        public void CommitCommandPlaybackObjects(
            object? playbackInfo,
            object? playbackControls)
        {
            var currentOwner = Volatile.Read(ref this._owner)
                ?? throw new ObjectDisposedException(nameof(NativeUse));
            currentOwner.CommitCommandPlaybackObjects(playbackInfo, playbackControls);
        }

        public void CommitMediaObjects(
            object? mediaProperties,
            object? thumbnail,
            object? genres)
        {
            var currentOwner = Volatile.Read(ref this._owner)
                ?? throw new ObjectDisposedException(nameof(NativeUse));
            currentOwner.CommitMediaObjects(mediaProperties, thumbnail, genres);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref this._owner, null)?.Exit();
        }
    }
}

internal sealed class GsmtcSessionRetiredException()
    : InvalidOperationException("The GSMTC session was retired before the native operation started.");