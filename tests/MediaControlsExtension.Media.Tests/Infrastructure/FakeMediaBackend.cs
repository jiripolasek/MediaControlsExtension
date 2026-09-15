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
    private readonly TaskCompletionSource _artworkStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _commandStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseArtwork = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
    private readonly List<MediaBackendCommand> _commands = [];
    private readonly List<(MediaBackendCommand Command, bool Completed)> _commandEvents = [];
    private readonly List<MediaArtworkKey> _artworkRequests = [];
    private MediaBackendSnapshot _snapshot = initialSnapshot;
    private MediaBackendSessionTarget? _blockedCommandTarget;
    private int _blockArtwork;
    private int _blockCommands;
    private int _blockSnapshotReads;
    private int _blockStart;
    private int _disposeCount;
    private int _failObservationInvalidations;
    private int _snapshotReadCount;

    public MediaBackendCommandResult CommandResult { get; set; } = new(
        MediaBackendCommandStatus.Completed,
        null);

    public Func<MediaBackendCommand, CancellationToken, Task<MediaBackendCommandResult>>? CommandHandler { get; set; }

    public Task ArtworkStarted => this._artworkStarted.Task;

    public Task CommandStarted => this._commandStarted.Task;

    public int DisposeCount => Volatile.Read(ref this._disposeCount);

    public int SnapshotReadCount => Volatile.Read(ref this._snapshotReadCount);

    public Task SnapshotReadStarted => this._snapshotReadStarted.Task;

    public Task StartStarted => this._startStarted.Task;

    public bool SignalOnStart { get; set; } = true;

    public Exception? StartFailure { get; set; }

    public Exception? SnapshotFailure { get; set; }

    public bool IgnoreCommandCancellation { get; set; }

    public bool IgnoreSnapshotCancellation { get; set; }

    public bool FailDisposal { get; set; }

    public Task? DisposalBarrier { get; set; }

    public MediaArtworkContent? Artwork { get; set; }

    public ImmutableArray<MediaBackendCommand> Commands
    {
        get
        {
            lock (this._stateLock)
            {
                return [.. this._commands];
            }
        }
    }

    public ImmutableArray<MediaArtworkKey> ArtworkRequests
    {
        get
        {
            lock (this._stateLock)
            {
                return [.. this._artworkRequests];
            }
        }
    }

    public ImmutableArray<(MediaBackendCommand Command, bool Completed)> CommandEvents
    {
        get
        {
            lock (this._stateLock)
            {
                return [.. this._commandEvents];
            }
        }
    }

    public void CompleteWatch(Exception? exception = null) => this._signals.Writer.TryComplete(exception);

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

    public void BlockArtwork() => Volatile.Write(ref this._blockArtwork, 1);

    public void ReleaseArtwork() => this._releaseArtwork.TrySetResult();

    public void BlockCommands(MediaBackendSessionTarget? target = null)
    {
        this._blockedCommandTarget = target;
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

    public void FailObservationInvalidations()
    {
        Volatile.Write(ref this._failObservationInvalidations, 1);
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
        if (this.StartFailure is { } failure)
        {
            throw failure;
        }
        if (Volatile.Read(ref this._blockStart) != 0)
        {
            await this._releaseStart.Task.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (this.SignalOnStart)
        {
            this.Signal(
                MediaBackendSignal.ObservationsChanged |
                MediaBackendSignal.SessionsChanged |
                MediaBackendSignal.CurrentSessionChanged);
        }
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
        if (this.SnapshotFailure is { } failure)
        {
            throw failure;
        }
        if (Volatile.Read(ref this._blockSnapshotReads) != 0)
        {
            this._snapshotReadStarted.TrySetResult();
            await this._releaseSnapshotReads.Task.WaitAsync(this.IgnoreSnapshotCancellation ? CancellationToken.None : cancellationToken)
                .ConfigureAwait(false);
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

        if (Volatile.Read(ref this._failObservationInvalidations) != 0)
        {
            throw new InvalidOperationException("Injected observation invalidation failure.");
        }
    }

    public async Task<MediaBackendCommandResult> ExecuteAsync(
        MediaBackendCommand command,
        CancellationToken cancellationToken)
    {
        lock (this._stateLock)
        {
            this._commands.Add(command);
            this._commandEvents.Add((command, false));
        }

        this._commandStarted.TrySetResult();
        try
        {
            var commandTarget = new MediaBackendSessionTarget(command.SessionId, command.BindingGeneration);
            if (Volatile.Read(ref this._blockCommands) != 0 &&
                (this._blockedCommandTarget is null || this._blockedCommandTarget == commandTarget))
            {
                await this._releaseCommands.Task.WaitAsync(this.IgnoreCommandCancellation ? CancellationToken.None : cancellationToken)
                    .ConfigureAwait(false);
            }

            return this.CommandHandler is { } handler
                ? await handler(command, cancellationToken).ConfigureAwait(false)
                : this.CommandResult;
        }
        finally
        {
            lock (this._stateLock)
            {
                this._commandEvents.Add((command, true));
            }
        }
    }

    public async ValueTask<MediaArtworkContent?> GetArtworkAsync(
        MediaArtworkKey key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MediaArtworkContent? artwork;
        lock (this._stateLock)
        {
            this._artworkRequests.Add(key);
            artwork = this.Artwork;
        }

        this._artworkStarted.TrySetResult();
        if (Volatile.Read(ref this._blockArtwork) != 0)
        {
            await this._releaseArtwork.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return artwork;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref this._disposeCount);
        this._signals.Writer.TryComplete();
        this._releaseArtwork.TrySetResult();
        this._releaseCommands.TrySetResult();
        this._releaseSnapshotReads.TrySetResult();
        this._releaseStart.TrySetResult();
        return this.FailDisposal
            ? ValueTask.FromException(new InvalidOperationException("Injected disposal failure."))
            : this.DisposalBarrier is { } barrier ? new ValueTask(barrier) : ValueTask.CompletedTask;
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
            [backendSessionId],
            MediaControlAvailability.Available)
        {
            Connection = MediaBackendConnectionState.Connected,
        };
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
            [new(currentSessionId)],
            MediaControlAvailability.Available)
        {
            Connection = MediaBackendConnectionState.Connected,
        };
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
            [new(currentSessionId)],
            MediaControlAvailability.Available)
        {
            Connection = MediaBackendConnectionState.Connected,
        };
    }

    private static MediaBackendSessionSnapshot CreateSession(
        MediaBackendSessionId backendSessionId,
        string title,
        long bindingGeneration,
        MediaPlaybackState playbackState,
        TimeSpan? position)
    {
        var source = new MediaSourceSnapshot($"Test Player {backendSessionId.Value}")
        {
            NativeApplication = new($"test.app.{backendSessionId.Value}"),
        };
        return new(
            backendSessionId,
            bindingGeneration,
            MediaPropertiesSnapshot.Empty(source) with { Title = title },
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