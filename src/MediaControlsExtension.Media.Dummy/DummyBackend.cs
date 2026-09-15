using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Dummy;

/// <summary>Simulates three players with random playlists and artwork; produces no audio or native sessions.</summary>
/// <remarks>One timer advances tracks; commands and track changes publish timestamped positions. Playlists and artwork are bounded.</remarks>
public sealed class DummyBackend : IMediaBackend
{
    private const MediaCapabilities Capabilities = MediaCapabilities.Play | MediaCapabilities.Pause | MediaCapabilities.Stop |
                                                   MediaCapabilities.SkipNext | MediaCapabilities.SkipPrevious;
    private const MediaBackendSignal Changed = MediaBackendSignal.ObservationsChanged | MediaBackendSignal.SessionsChanged |
                                              MediaBackendSignal.CurrentSessionChanged | MediaBackendSignal.BackendsChanged;
    private readonly object _gate = new();
    private readonly Random _random;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<MediaBackendSignal> _signals = Channel.CreateBounded<MediaBackendSignal>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private MediaBackendSnapshot _snapshot = new(0, [], [], MediaControlAvailability.Unavailable);
    private Session[] _sessions = [];
    private Task? _monitor;
    private Task? _disposeTask;
    private long _lastTimestamp;
    private int _currentSession;
    private bool _disposed;

    /// <summary>Creates a dormant simulator; each player gets twelve random tracks at startup.</summary>
    /// <param name="seed">Optional seed for reproducible playlists; omitted for new random data each run.</param>
    public DummyBackend(int? seed = null)
    {
        this._random = seed is { } value ? new Random(value) : new Random();
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this._gate)
        {
            ObjectDisposedException.ThrowIf(this._disposed, this);
            if (this._monitor is not null) { throw new InvalidOperationException("The dummy provider has already started."); }
            this._sessions = new Session[3];
            for (var i = 0; i < this._sessions.Length; i++)
            {
                var session = new Session(i + 1, DummyTrack.CreatePlaylist(this._random));
                session.SelectTrack(0);
                session.Playback = i == 0 ? MediaPlaybackState.Playing : MediaPlaybackState.Paused;
                this._sessions[i] = session;
            }

            this._lastTimestamp = Stopwatch.GetTimestamp();
            this.Publish();
            this._monitor = this.MonitorAsync();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<MediaBackendSignal> WatchAsync(CancellationToken cancellationToken) =>
        this._signals.Reader.ReadAllAsync(cancellationToken);

    /// <inheritdoc />
    public Task<MediaBackendSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this._gate) { return Task.FromResult(this._snapshot); }
    }

    /// <inheritdoc />
    public void InvalidateObservations(ImmutableArray<MediaBackendObservationRequest> requests)
    {
        // Commands and track changes publish timestamped positions for local extrapolation.
    }

    /// <inheritdoc />
    public Task<MediaBackendCommandResult> ExecuteAsync(MediaBackendCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this._gate)
        {
            if (this._disposed || this._monitor is null) { return Result(MediaBackendCommandStatus.Unavailable); }
            if (command.BindingGeneration != 1 || command.SessionId.Value < 1 || command.SessionId.Value > this._sessions.Length)
            {
                return Result(MediaBackendCommandStatus.SessionGone);
            }

            var session = this._sessions[(int)command.SessionId.Value - 1];
            switch (command.Operation)
            {
                case MediaOperation.Play:
                    this.AdvancePositions();
                    session.Playback = MediaPlaybackState.Playing;
                    this._currentSession = (int)command.SessionId.Value - 1;
                    break;
                case MediaOperation.Pause:
                    this.AdvancePositions();
                    session.Playback = MediaPlaybackState.Paused;
                    break;
                case MediaOperation.Stop:
                    this.AdvancePositions();
                    session.Playback = MediaPlaybackState.Stopped;
                    session.Position = TimeSpan.Zero;
                    break;
                case MediaOperation.SkipNext:
                case MediaOperation.SkipPrevious:
                    this.AdvancePositions();
                    session.SelectTrack(session.TrackIndex + (command.Operation == MediaOperation.SkipNext ? 1 : -1));
                    break;
                default:
                    return Result(MediaBackendCommandStatus.Unsupported);
            }

            this.Publish();
            return Result(MediaBackendCommandStatus.Completed);
        }
    }

    /// <inheritdoc />
    public ValueTask<MediaArtworkContent?> GetArtworkAsync(MediaArtworkKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this._gate)
        {
            if (this._disposed || key.SessionId.Value < 1 || key.SessionId.Value > this._sessions.Length)
            {
                return ValueTask.FromResult<MediaArtworkContent?>(null);
            }

            var session = this._sessions[(int)key.SessionId.Value - 1];
            return ValueTask.FromResult(key == session.Metadata.Artwork
                ? session.Artwork ??= session.Tracks[session.TrackIndex].CreateArtwork()
                : null);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (this._gate)
        {
            if (this._disposeTask is not null) { return new(this._disposeTask); }
            this._disposed = true;
            this._sessions = [];
            this._snapshot = new(this._snapshot.Revision + 1, [], [], MediaControlAvailability.Unavailable)
            {
                Connection = MediaBackendConnectionState.Disconnected,
            };
            this._signals.Writer.TryComplete();
            this._lifetime.Cancel();
            this._disposeTask = this.StopMonitorAsync();
            return new(this._disposeTask);
        }
    }

    private async Task StopMonitorAsync()
    {
        try
        {
            if (this._monitor is not null) { await this._monitor.ConfigureAwait(false); }
        }
        catch (OperationCanceledException) when (this._lifetime.IsCancellationRequested) { }
        finally { this._lifetime.Dispose(); }
    }

    private async Task MonitorAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(this._lifetime.Token).ConfigureAwait(false))
        {
            lock (this._gate)
            {
                if (this._disposed) { return; }
                if (this.AdvancePositions()) { this.Publish(); }
            }
        }
    }

    private bool AdvancePositions()
    {
        var timestamp = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(this._lastTimestamp, timestamp);
        this._lastTimestamp = timestamp;
        var changed = false;
        foreach (var session in this._sessions)
        {
            if (session.Playback != MediaPlaybackState.Playing) { continue; }
            session.Position += elapsed;
            if (session.Position >= session.Tracks[session.TrackIndex].Duration)
            {
                session.SelectTrack(session.TrackIndex + 1);
                changed = true;
            }
        }

        return changed;
    }

    private void Publish()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshots = ImmutableArray.CreateBuilder<MediaBackendSessionSnapshot>(this._sessions.Length);
        foreach (var session in this._sessions)
        {
            var duration = session.Tracks[session.TrackIndex].Duration;
            snapshots.Add(new(new(session.Id), 1, session.Metadata,
                new(TimeSpan.Zero, duration, TimeSpan.Zero, duration, session.Position, now), session.Playback, Capabilities));
        }

        this._snapshot = new(this._snapshot.Revision + 1, snapshots.MoveToImmutable(),
            [new(this._sessions[this._currentSession].Id)], MediaControlAvailability.Available)
        {
            Connection = MediaBackendConnectionState.Connected,
        };
        this._signals.Writer.TryWrite(Changed);
    }

    private static Task<MediaBackendCommandResult> Result(MediaBackendCommandStatus status) => Task.FromResult(new MediaBackendCommandResult(status, null));

    private sealed class Session(int id, DummyTrack[] tracks)
    {
        public int Id { get; } = id;
        public DummyTrack[] Tracks { get; } = tracks;
        public int TrackIndex { get; private set; }
        public MediaPlaybackState Playback { get; set; }
        public TimeSpan Position { get; set; }
        public MediaPropertiesSnapshot Metadata { get; private set; } = MediaPropertiesSnapshot.Empty(new());
        public MediaArtworkContent? Artwork { get; set; }
        private long _artworkVersion;

        public void SelectTrack(int index)
        {
            this.TrackIndex = (index + this.Tracks.Length) % this.Tracks.Length;
            this.Position = TimeSpan.Zero;
            this.Artwork = null;
            var track = this.Tracks[this.TrackIndex];
            this.Metadata = new(new("Dummy player " + this.Id.ToString(CultureInfo.InvariantCulture)),
                track.Title, track.Artist, track.Album, track.Artist, "Simulated media", [track.Genre],
                this.TrackIndex + 1, this.Tracks.Length, MediaContentType.Music, new(new(this.Id), ++this._artworkVersion));
        }
    }
}