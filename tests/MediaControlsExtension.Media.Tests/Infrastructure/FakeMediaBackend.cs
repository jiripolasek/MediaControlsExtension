// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;

internal sealed class FakeMediaBackend(MediaBackendSnapshot initialSnapshot) : IMediaBackend
{
    private readonly TaskCompletionSource _commandStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseCommands = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseSnapshotReads = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _snapshotReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _startStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<MediaBackendSignal> _signals = Channel.CreateBounded<MediaBackendSignal>(
        new BoundedChannelOptions(1)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false,
        });
    private readonly Lock _stateLock = new();
    private readonly List<ImmutableArray<MediaBackendObservationRequest>> _observationInvalidations = [];
    private MediaBackendSnapshot _snapshot = initialSnapshot;
    private int _blockCommands;
    private int _blockSnapshotReads;
    private int _blockStart;
    private int _disposeCount;
    private int _snapshotReadCount;

    public MediaBackendCommandResult CommandResult { get; set; } = new(
        MediaBackendCommandStatus.Completed,
        null);

    public Task CommandStarted => this._commandStarted.Task;

    public int DisposeCount => Volatile.Read(ref this._disposeCount);

    public int SnapshotReadCount => Volatile.Read(ref this._snapshotReadCount);

    public Task SnapshotReadStarted => this._snapshotReadStarted.Task;

    public Task StartStarted => this._startStarted.Task;

    public ImmutableArray<ImmutableArray<MediaBackendObservationRequest>> ObservationInvalidations
    {
        get
        {
            lock (this._stateLock)
            {
                return [.. this._observationInvalidations];
            }
        }
    }

    public void BlockCommands()
    {
        Volatile.Write(ref this._blockCommands, 1);
    }

    public void ReleaseCommands()
    {
        this._releaseCommands.TrySetResult();
    }

    public void BlockStart()
    {
        Volatile.Write(ref this._blockStart, 1);
    }

    public void ReleaseStart()
    {
        this._releaseStart.TrySetResult();
    }

    public void SetSnapshot(MediaBackendSnapshot snapshot)
    {
        this.SetSnapshotWithoutSignal(snapshot);
        this.Signal(MediaBackendSignal.ObservationsChanged);
    }

    public void BlockSnapshotReads()
    {
        Volatile.Write(ref this._blockSnapshotReads, 1);
    }

    public void ReleaseSnapshotReads()
    {
        this._releaseSnapshotReads.TrySetResult();
    }

    public void Signal(MediaBackendSignal signal)
    {
        this._signals.Writer.TryWrite(signal);
    }

    public void SetSnapshotWithoutSignal(MediaBackendSnapshot snapshot)
    {
        lock (this._stateLock)
        {
            this._snapshot = snapshot;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        this._startStarted.TrySetResult();
        if (Volatile.Read(ref this._blockStart) != 0)
        {
            await this._releaseStart.Task.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        this.Signal(
            MediaBackendSignal.ObservationsChanged |
            MediaBackendSignal.SessionsChanged |
            MediaBackendSignal.CurrentSessionChanged);
    }

    public async IAsyncEnumerable<MediaBackendSignal> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var signal in this._signals.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return signal;
        }
    }

    public async Task<MediaBackendSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref this._snapshotReadCount);
        if (Volatile.Read(ref this._blockSnapshotReads) != 0)
        {
            this._snapshotReadStarted.TrySetResult();
            await this._releaseSnapshotReads.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        lock (this._stateLock)
        {
            return this._snapshot;
        }
    }

    public void InvalidateObservations(
        ImmutableArray<MediaBackendObservationRequest> requests)
    {
        lock (this._stateLock)
        {
            this._observationInvalidations.Add(requests);
        }
    }

    public async Task<MediaBackendCommandResult> ExecuteAsync(
        MediaBackendCommand command,
        CancellationToken cancellationToken)
    {
        this._commandStarted.TrySetResult();
        if (Volatile.Read(ref this._blockCommands) != 0)
        {
            await this._releaseCommands.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return this.CommandResult;
    }

    public ValueTask<MediaArtworkContent?> GetArtworkAsync(
        MediaArtworkKey key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<MediaArtworkContent?>(null);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref this._disposeCount);
        this._signals.Writer.TryComplete();
        this._releaseCommands.TrySetResult();
        this._releaseSnapshotReads.TrySetResult();
        this._releaseStart.TrySetResult();
        return ValueTask.CompletedTask;
    }

    public static MediaBackendSnapshot CreateSnapshot(
        long revision,
        string title,
        long bindingGeneration = 1,
        MediaPlaybackState playbackState = MediaPlaybackState.Paused,
        long sessionId = 1,
        TimeSpan? position = null)
    {
        var backendSessionId = new MediaBackendSessionId(sessionId);
        return new(
            revision,
            [CreateSession(
                backendSessionId,
                title,
                bindingGeneration,
                playbackState,
                position)],
            backendSessionId,
            MediaControlAvailability.Available);
    }

    public static MediaBackendSnapshot CreateSnapshot(
        long revision,
        long currentSessionId,
        params (long SessionId, string Title)[] sessions)
    {
        return new(
            revision,
            sessions
                .Select(session => CreateSession(
                    new(session.SessionId),
                    session.Title,
                    1,
                    MediaPlaybackState.Paused,
                    null))
                .ToImmutableArray(),
            new(currentSessionId),
            MediaControlAvailability.Available);
    }

    public static MediaBackendSnapshot CreateSnapshot(
        long revision,
        long currentSessionId,
        params (long SessionId, string Title, MediaPlaybackState PlaybackState)[] sessions)
    {
        return new(
            revision,
            sessions
                .Select(session => CreateSession(
                    new(session.SessionId),
                    session.Title,
                    1,
                    session.PlaybackState,
                    null))
                .ToImmutableArray(),
            new(currentSessionId),
            MediaControlAvailability.Available);
    }

    private static MediaBackendSessionSnapshot CreateSession(
        MediaBackendSessionId backendSessionId,
        string title,
        long bindingGeneration,
        MediaPlaybackState playbackState,
        TimeSpan? position)
    {
        var application = new MediaApplicationSnapshot(
            $"test.app.{backendSessionId.Value}",
            $"Test Player {backendSessionId.Value}",
            null,
            null);
        return new(
            backendSessionId,
            bindingGeneration,
            MediaPropertiesSnapshot.Empty(application) with { Title = title },
            MediaTimelinePropertiesSnapshot.Empty with
            {
                EndTime = TimeSpan.FromMinutes(3),
                MaxSeekTime = TimeSpan.FromMinutes(3),
                Position = position ?? TimeSpan.Zero,
            },
            playbackState,
            MediaCapabilities.Play |
            MediaCapabilities.Pause |
            MediaCapabilities.Stop |
            MediaCapabilities.SkipNext |
            MediaCapabilities.SkipPrevious);
    }
}
