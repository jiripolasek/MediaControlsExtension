// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using JPSoftworks.MediaControlsExtension.Media.Diagnostics;
using Microsoft.Extensions.Logging;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace JPSoftworks.MediaControlsExtension.Media.Infrastructure.Gsmtc;

/// <summary>
/// Owns every native GSMTC object. No native manager or session reference
/// crosses the media-project boundary.
/// </summary>
internal sealed class GsmtcBackend : IMediaBackend
{
    private const ulong MaxArtworkBytes = 32 * 1024 * 1024;

    [Flags]
    private enum SessionObservationChanges
    {
        None = 0,
        Playback = 1 << 0,
        Timeline = 1 << 1,
        MediaProperties = 1 << 2,
        All = Playback | Timeline | MediaProperties,
    }

    private readonly record struct SessionObservationPlan(
        MediaBackendSessionSnapshot? PreviousSnapshot,
        SessionObservationChanges Changes);

    private readonly GsmtcControlGate _controlGate;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly ILogger _logger;
    private readonly GsmtcObservationGate _observationGate;
    private readonly Channel<MediaBackendSignal> _signals;
    private readonly Lock _stateLock = new();
    private readonly Dictionary<MediaBackendSessionId, SessionBinding> _bindings = [];

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private MediaBackendSessionId? _currentSessionId;
    private long _nextArtworkVersion;
    private long _nextBackendRevision;
    private long _nextSessionId;
    private int _bindingsDirty = 1;
    private int _disposeState;
    private int _startState;

    public GsmtcBackend(ILogger<GsmtcBackend> logger)
    {
        this._logger = logger;
        this._controlGate = new(logger);
        this._observationGate = new(logger);
        this._signals = Channel.CreateBounded<MediaBackendSignal>(new BoundedChannelOptions(1)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false,
        });
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this._disposeState) != 0, this);
        if (Interlocked.CompareExchange(ref this._startState, 1, 0) != 0)
        {
            throw new InvalidOperationException("The GSMTC backend has already been started.");
        }

        var manager = await this._controlGate.RunAsync(
            async () => await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(),
            "RequestSessionManager",
            cancellationToken).ConfigureAwait(false);

        // Install the manager only after the gated acquisition has definitively
        // succeeded. A timed-out native request may still complete later.
        cancellationToken.ThrowIfCancellationRequested();
        lock (this._stateLock)
        {
            ObjectDisposedException.ThrowIf(this._disposeState != 0, this);
            manager.SessionsChanged += this.ManagerOnSessionsChanged;
            manager.CurrentSessionChanged += this.ManagerOnCurrentSessionChanged;
            this._manager = manager;
            this._startState = 2;
        }

        this.SignalStateChanged();
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this._disposeState) != 0, this);
        if (Interlocked.Exchange(ref this._bindingsDirty, 0) != 0)
        {
            try
            {
                await this.RefreshBindingsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Volatile.Write(ref this._bindingsDirty, 1);
                throw;
            }
        }

        SessionBinding[] bindings;
        MediaBackendSessionId? currentSessionId;
        lock (this._stateLock)
        {
            bindings = [.. this._bindings.Values.OrderBy(static binding => binding.Id.Value)];
            currentSessionId = this._currentSessionId;
        }

        var snapshots = ImmutableArray.CreateBuilder<MediaBackendSessionSnapshot>(bindings.Length);
        foreach (var binding in bindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = binding.BeginObservation();
            if (plan.Changes == SessionObservationChanges.None)
            {
                snapshots.Add(plan.PreviousSnapshot!);
                continue;
            }

            try
            {
                var observed = await this._observationGate.RunAsync(
                    () => ReadSessionAsync(binding, plan),
                    $"ReadSession:{binding.ApplicationId}",
                    cancellationToken).ConfigureAwait(false);
                binding.CompleteObservation(observed);
                snapshots.Add(observed);
            }
            catch (GsmtcObservationBlockedException)
            {
                binding.RestoreObservation(plan.Changes);
                snapshots.Add(binding.LastSnapshot ?? CreateFallbackSnapshot(binding));
            }
            catch (Exception ex) when (GsmtcErrors.IndicatesStaleSession(ex))
            {
                binding.RestoreObservation(plan.Changes);
                MediaLog.StaleSession(this._logger, binding.ApplicationId, "snapshot observation");
                snapshots.Add(binding.LastSnapshot ?? CreateFallbackSnapshot(binding));
                this.InvalidateBindings();
            }
            catch (Exception ex)
            {
                binding.RestoreObservation(plan.Changes);
                MediaLog.SessionObservationFailed(this._logger, binding.ApplicationId, ex);
                snapshots.Add(binding.LastSnapshot ?? CreateFallbackSnapshot(binding));
            }
        }

        return new(
            Interlocked.Increment(ref this._nextBackendRevision),
            snapshots.MoveToImmutable(),
            currentSessionId,
            this._controlGate.IsCircuitOpen
                ? MediaControlAvailability.CircuitOpen
                : MediaControlAvailability.Available);
    }

    public void InvalidateObservations()
    {
        lock (this._stateLock)
        {
            if (this._disposeState != 0)
            {
                return;
            }

            foreach (var binding in this._bindings.Values)
            {
                binding.Invalidate(SessionObservationChanges.All);
            }
        }
    }

    public async Task<MediaBackendCommandResult> ExecuteAsync(
        MediaBackendCommand command,
        CancellationToken cancellationToken)
    {
        SessionBinding? target;
        SessionBinding[] sessionsToPause;
        lock (this._stateLock)
        {
            this._bindings.TryGetValue(command.SessionId, out target);
            sessionsToPause = command.SessionsToPause
                .Select(id => this._bindings.GetValueOrDefault(id))
                .Where(static binding => binding is not null)
                .Cast<SessionBinding>()
                .ToArray();
        }

        if (target is null || target.Generation != command.BindingGeneration)
        {
            return new(MediaBackendCommandStatus.SessionGone, "The target session was replaced or removed.");
        }

        try
        {
            var success = await this._controlGate.RunCommandAsync(
                async () =>
                {
                    foreach (var other in sessionsToPause)
                    {
                        try
                        {
                            if (await other.Session.TryPauseAsync())
                            {
                                other.Invalidate(SessionObservationChanges.Playback);
                            }
                        }
                        catch (Exception ex)
                        {
                            MediaLog.PauseOtherSessionFailed(this._logger, other.ApplicationId, ex);
                        }
                    }

                    return await ExecuteOperationAsync(target.Session, command.Operation).ConfigureAwait(false);
                },
                command.Operation.ToString(),
                cancellationToken).ConfigureAwait(false);
            if (success)
            {
                target.Invalidate(ChangesForOperation(command.Operation));
            }

            return success
                ? new(MediaBackendCommandStatus.Completed, null)
                : new(MediaBackendCommandStatus.Failed, "GSMTC rejected the requested operation.");
        }
        catch (GsmtcControlBusyException ex)
        {
            return new(MediaBackendCommandStatus.Unavailable, ex.Message);
        }
        catch (GsmtcControlCircuitOpenException ex)
        {
            return new(MediaBackendCommandStatus.Unavailable, ex.Message);
        }
        catch (Exception ex) when (GsmtcErrors.IndicatesStaleSession(ex))
        {
            this.InvalidateBindings();
            return new(MediaBackendCommandStatus.SessionGone, ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return new(MediaBackendCommandStatus.Unsupported, ex.Message);
        }
        catch (Exception ex)
        {
            return new(MediaBackendCommandStatus.Failed, ex.Message);
        }
    }

    public async ValueTask<MediaArtworkContent?> GetArtworkAsync(
        MediaArtworkKey key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SessionBinding? binding;
        lock (this._stateLock)
        {
            this._bindings.TryGetValue(
                new MediaBackendSessionId(key.SessionId.Value),
                out binding);
        }

        if (binding is null ||
            !binding.TryGetArtworkReference(key.Version, out var reference))
        {
            return null;
        }

        try
        {
            return await this._observationGate.RunAsync(
                () => ReadArtworkAsync(reference),
                $"ReadArtwork:{binding.ApplicationId}",
                cancellationToken).ConfigureAwait(false);
        }
        catch (GsmtcObservationBlockedException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this._disposeState, 1) != 0)
        {
            return;
        }

        this._disposeCts.Cancel();
        this._signals.Writer.TryComplete();

        GlobalSystemMediaTransportControlsSessionManager? manager;
        SessionBinding[] bindings;
        lock (this._stateLock)
        {
            manager = this._manager;
            this._manager = null;
            bindings = [.. this._bindings.Values];
            this._bindings.Clear();
            this._currentSessionId = null;
        }

        if (manager is not null && !this._controlGate.IsCircuitOpen)
        {
            try
            {
                await this._controlGate.RunAsync(
                    () =>
                    {
                        manager.SessionsChanged -= this.ManagerOnSessionsChanged;
                        manager.CurrentSessionChanged -= this.ManagerOnCurrentSessionChanged;
                        foreach (var binding in bindings)
                        {
                            binding.Unhook();
                        }

                        return Task.FromResult(true);
                    },
                    "DisposeSessionManager",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        this._disposeCts.Dispose();
    }

    private static SessionObservationChanges ChangesForOperation(MediaOperation operation)
    {
        return operation is MediaOperation.SkipNext or MediaOperation.SkipPrevious
            ? SessionObservationChanges.All
            : SessionObservationChanges.Playback;
    }

    private static MediaBackendSessionSnapshot CreateFallbackSnapshot(SessionBinding binding)
    {
        var application = new MediaApplicationSnapshot(
            binding.ApplicationId,
            binding.ApplicationId,
            null,
            null);
        return new(
            binding.Id,
            binding.Generation,
            MediaPropertiesSnapshot.Empty(application),
            MediaTimelinePropertiesSnapshot.Empty,
            MediaPlaybackState.Unknown,
            MediaCapabilities.None);
    }

    private static async Task<MediaBackendSessionSnapshot> ReadSessionAsync(
        SessionBinding binding,
        SessionObservationPlan plan)
    {
        var previous = plan.PreviousSnapshot;
        var mediaProperties = previous?.MediaProperties;
        var timelineProperties = previous?.TimelineProperties;
        var playbackState = previous?.PlaybackState ?? MediaPlaybackState.Unknown;
        var capabilities = previous?.Capabilities ?? MediaCapabilities.None;

        if ((plan.Changes & SessionObservationChanges.Playback) != 0)
        {
            var playbackInfo = binding.Session.GetPlaybackInfo();
            playbackState = MapPlaybackState(playbackInfo?.PlaybackStatus);
            capabilities = MapCapabilities(playbackInfo);
        }

        if ((plan.Changes & SessionObservationChanges.Timeline) != 0)
        {
            var timeline = binding.Session.GetTimelineProperties();
            timelineProperties = new(
                timeline.StartTime,
                timeline.EndTime,
                timeline.MinSeekTime,
                timeline.MaxSeekTime,
                timeline.Position,
                timeline.LastUpdatedTime);
        }

        if ((plan.Changes & SessionObservationChanges.MediaProperties) != 0)
        {
            var properties = await binding.Session.TryGetMediaPropertiesAsync();
            var application = new MediaApplicationSnapshot(
                binding.ApplicationId,
                binding.ApplicationId,
                null,
                null);
            var artwork = binding.UpdateArtworkReference(properties?.Thumbnail);
            mediaProperties = properties is null
                ? MediaPropertiesSnapshot.Empty(application)
                : new(
                    application,
                    properties.Title ?? string.Empty,
                    properties.Artist ?? string.Empty,
                    properties.AlbumTitle ?? string.Empty,
                    properties.AlbumArtist ?? string.Empty,
                    properties.Subtitle ?? string.Empty,
                    properties.Genres?.ToImmutableArray() ?? [],
                    properties.TrackNumber,
                    properties.AlbumTrackCount,
                    MapContentType(properties.PlaybackType),
                    artwork);
        }

        return new(
            binding.Id,
            binding.Generation,
            mediaProperties!,
            timelineProperties!,
            playbackState,
            capabilities);
    }

    private static MediaCapabilities MapCapabilities(
        GlobalSystemMediaTransportControlsSessionPlaybackInfo? playbackInfo)
    {
        var capabilities = MediaCapabilities.None;
        var controls = playbackInfo?.Controls;
        if (controls?.IsPlayEnabled == true)
        {
            capabilities |= MediaCapabilities.Play;
        }

        if (controls?.IsPauseEnabled == true)
        {
            capabilities |= MediaCapabilities.Pause;
        }

        if (controls?.IsStopEnabled == true)
        {
            capabilities |= MediaCapabilities.Stop;
        }

        if (controls?.IsNextEnabled == true)
        {
            capabilities |= MediaCapabilities.SkipNext;
        }

        if (controls?.IsPreviousEnabled == true)
        {
            capabilities |= MediaCapabilities.SkipPrevious;
        }

        if (controls?.IsShuffleEnabled == true)
        {
            capabilities |= MediaCapabilities.ToggleShuffle;
        }

        if (controls?.IsRepeatEnabled == true)
        {
            capabilities |= MediaCapabilities.ToggleRepeat;
        }

        return capabilities;
    }

    private static MediaContentType MapContentType(MediaPlaybackType? playbackType)
    {
        return playbackType switch
        {
            MediaPlaybackType.Music => MediaContentType.Music,
            MediaPlaybackType.Video => MediaContentType.Video,
            MediaPlaybackType.Image => MediaContentType.Image,
            _ => MediaContentType.Unknown,
        };
    }

    private static MediaPlaybackState MapPlaybackState(
        GlobalSystemMediaTransportControlsSessionPlaybackStatus? playbackStatus)
    {
        return playbackStatus switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => MediaPlaybackState.Closed,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => MediaPlaybackState.Opened,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => MediaPlaybackState.Changing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlaybackState.Stopped,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackState.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackState.Paused,
            _ => MediaPlaybackState.Unknown,
        };
    }

    private static async Task<MediaArtworkContent?> ReadArtworkAsync(
        IRandomAccessStreamReference reference)
    {
        using var stream = await reference.OpenReadAsync();
        if (stream.Size == 0 || stream.Size > MaxArtworkBytes)
        {
            return null;
        }

        var bytes = new byte[(int)stream.Size];
        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            var loaded = await reader.LoadAsync((uint)stream.Size);
            if (loaded == 0)
            {
                return null;
            }

            if (loaded != bytes.Length)
            {
                bytes = new byte[loaded];
            }

            reader.ReadBytes(bytes);
        }

        return new(
            DetectArtworkContentType(bytes),
            bytes,
            Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static string DetectArtworkContentType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }))
        {
            return "image/png";
        }

        if (bytes.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }))
        {
            return "image/jpeg";
        }

        if (bytes.StartsWith("GIF8"u8))
        {
            return "image/gif";
        }

        if (bytes.Length >= 12 &&
            bytes[..4].SequenceEqual("RIFF"u8) &&
            bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        return "application/octet-stream";
    }

    private static async Task<bool> ExecuteOperationAsync(
        GlobalSystemMediaTransportControlsSession session,
        MediaOperation operation)
    {
        return operation switch
        {
            MediaOperation.Play => await session.TryPlayAsync(),
            MediaOperation.Pause => await session.TryPauseAsync(),
            MediaOperation.Stop => await session.TryStopAsync(),
            MediaOperation.SkipNext => await session.TrySkipNextAsync(),
            MediaOperation.SkipPrevious => await session.TrySkipPreviousAsync(),
            MediaOperation.ToggleShuffle => await ToggleShuffleAsync(session),
            MediaOperation.ToggleRepeat => await ToggleRepeatAsync(session),
            _ => throw new NotSupportedException($"Media operation {operation} is not a primitive GSMTC operation."),
        };
    }

    private static async Task<bool> ToggleShuffleAsync(
        GlobalSystemMediaTransportControlsSession session)
    {
        var playbackInfo = session.GetPlaybackInfo();
        if (playbackInfo?.Controls.IsShuffleEnabled != true)
        {
            return false;
        }

        return await session.TryChangeShuffleActiveAsync(!(playbackInfo.IsShuffleActive ?? false));
    }

    private static async Task<bool> ToggleRepeatAsync(
        GlobalSystemMediaTransportControlsSession session)
    {
        var playbackInfo = session.GetPlaybackInfo();
        if (playbackInfo?.Controls.IsRepeatEnabled != true)
        {
            return false;
        }

        var nextMode = playbackInfo.AutoRepeatMode switch
        {
            MediaPlaybackAutoRepeatMode.None => MediaPlaybackAutoRepeatMode.Track,
            MediaPlaybackAutoRepeatMode.Track => MediaPlaybackAutoRepeatMode.List,
            MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.None,
            { } current => current,
            _ => MediaPlaybackAutoRepeatMode.None,
        };
        return await session.TryChangeAutoRepeatModeAsync(nextMode);
    }

    private async Task RefreshBindingsAsync(CancellationToken cancellationToken)
    {
        GlobalSystemMediaTransportControlsSessionManager manager;
        SessionBinding[] existingBindings;
        lock (this._stateLock)
        {
            manager = this._manager ?? throw new InvalidOperationException("The GSMTC backend is not started.");
            existingBindings = [.. this._bindings.Values];
        }

        await this._controlGate.RunAsync(
            () =>
            {
                var sessions = manager.GetSessions() ?? [];
                var currentSession = manager.GetCurrentSession();
                var availableExisting = existingBindings.ToList();
                var nextBindings = new Dictionary<MediaBackendSessionId, SessionBinding>();
                var replacedBindings = new List<SessionBinding>();

                foreach (var session in sessions)
                {
                    string applicationId;
                    try
                    {
                        applicationId = session.SourceAppUserModelId;
                    }
                    catch (Exception ex) when (GsmtcErrors.IndicatesStaleSession(ex))
                    {
                        continue;
                    }

                    var existing = FindExistingBinding(availableExisting, session, applicationId);
                    SessionBinding binding;
                    if (existing is null)
                    {
                        binding = new(
                            this,
                            new(Interlocked.Increment(ref this._nextSessionId)),
                            1,
                            applicationId,
                            session);
                        binding.Hook();
                    }
                    else if (ReferenceEquals(existing.Session, session))
                    {
                        binding = existing;
                        availableExisting.Remove(existing);
                    }
                    else
                    {
                        binding = new(
                            this,
                            existing.Id,
                            existing.Generation + 1,
                            applicationId,
                            session);
                        binding.SeedSnapshot(existing.LastSnapshot);
                        binding.Hook();
                        availableExisting.Remove(existing);
                        replacedBindings.Add(existing);
                    }

                    nextBindings.Add(binding.Id, binding);
                }

                var currentSessionId = FindCurrentSessionId(nextBindings.Values, currentSession);
                lock (this._stateLock)
                {
                    this._bindings.Clear();
                    foreach (var (id, binding) in nextBindings)
                    {
                        this._bindings.Add(id, binding);
                    }

                    this._currentSessionId = currentSessionId;
                }

                foreach (var removed in availableExisting.Concat(replacedBindings))
                {
                    removed.Unhook();
                }

                return Task.FromResult(true);
            },
            "RefreshSessions",
            cancellationToken).ConfigureAwait(false);
    }

    private static SessionBinding? FindExistingBinding(
        IReadOnlyCollection<SessionBinding> existingBindings,
        GlobalSystemMediaTransportControlsSession session,
        string applicationId)
    {
        var referenceMatch = existingBindings.FirstOrDefault(
            binding => ReferenceEquals(binding.Session, session));
        if (referenceMatch is not null)
        {
            return referenceMatch;
        }

        var applicationMatches = existingBindings
            .Where(binding => string.Equals(
                binding.ApplicationId,
                applicationId,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        return applicationMatches.Length == 1 ? applicationMatches[0] : null;
    }

    private static MediaBackendSessionId? FindCurrentSessionId(
        IEnumerable<SessionBinding> bindings,
        GlobalSystemMediaTransportControlsSession? currentSession)
    {
        if (currentSession is null)
        {
            return null;
        }

        var bindingArray = bindings.ToArray();
        var referenceMatch = bindingArray.FirstOrDefault(
            binding => ReferenceEquals(binding.Session, currentSession));
        if (referenceMatch is not null)
        {
            return referenceMatch.Id;
        }

        try
        {
            var applicationId = currentSession.SourceAppUserModelId;
            var applicationMatches = bindingArray
                .Where(binding => string.Equals(
                    binding.ApplicationId,
                    applicationId,
                    StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            return applicationMatches.Length == 1 ? applicationMatches[0].Id : null;
        }
        catch (Exception ex) when (GsmtcErrors.IndicatesStaleSession(ex))
        {
            return null;
        }
    }

    private void ManagerOnSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args)
    {
        this.InvalidateBindings();
    }

    private void ManagerOnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        this.InvalidateBindings();
    }

    private void InvalidateBindings()
    {
        Volatile.Write(ref this._bindingsDirty, 1);
        this.SignalStateChanged();
    }

    private void SignalStateChanged()
    {
        if (Volatile.Read(ref this._disposeState) == 0)
        {
            this._signals.Writer.TryWrite(MediaBackendSignal.StateChanged);
        }
    }

    private sealed class SessionBinding(
        GsmtcBackend owner,
        MediaBackendSessionId id,
        long generation,
        string applicationId,
        GlobalSystemMediaTransportControlsSession session)
    {
        private readonly Lock _stateLock = new();
        private IRandomAccessStreamReference? _artworkReference;
        private bool _artworkChanged = true;
        private long _artworkVersion;
        private MediaBackendSessionSnapshot? _lastSnapshot;
        private SessionObservationChanges _pendingChanges = SessionObservationChanges.All;

        public MediaBackendSessionId Id { get; } = id;

        public long Generation { get; } = generation;

        public string ApplicationId { get; } = applicationId;

        public GlobalSystemMediaTransportControlsSession Session { get; } = session;

        public MediaBackendSessionSnapshot? LastSnapshot
        {
            get
            {
                lock (this._stateLock)
                {
                    return this._lastSnapshot;
                }
            }
        }

        public SessionObservationPlan BeginObservation()
        {
            lock (this._stateLock)
            {
                var plan = new SessionObservationPlan(
                    this._lastSnapshot,
                    this._pendingChanges);
                this._pendingChanges = SessionObservationChanges.None;
                return plan;
            }
        }

        public void CompleteObservation(MediaBackendSessionSnapshot snapshot)
        {
            lock (this._stateLock)
            {
                this._lastSnapshot = snapshot;
            }
        }

        public void RestoreObservation(SessionObservationChanges changes)
        {
            this.Invalidate(changes);
        }

        public void Invalidate(SessionObservationChanges changes)
        {
            lock (this._stateLock)
            {
                this._pendingChanges |= changes;
                if ((changes & SessionObservationChanges.MediaProperties) != 0)
                {
                    this._artworkChanged = true;
                }
            }
        }

        public void SeedSnapshot(MediaBackendSessionSnapshot? snapshot)
        {
            lock (this._stateLock)
            {
                this._lastSnapshot = snapshot;
            }
        }

        public MediaArtworkKey? UpdateArtworkReference(
            IRandomAccessStreamReference? reference)
        {
            lock (this._stateLock)
            {
                this._artworkReference = reference;
                var artworkChanged = this._artworkChanged;
                if ((this._pendingChanges & SessionObservationChanges.MediaProperties) == 0)
                {
                    this._artworkChanged = false;
                }

                if (reference is null)
                {
                    return null;
                }

                if (artworkChanged || this._artworkVersion == 0)
                {
                    this._artworkVersion = Interlocked.Increment(
                        ref owner._nextArtworkVersion);
                }

                return new(new(this.Id.Value), this._artworkVersion);
            }
        }

        public bool TryGetArtworkReference(
            long version,
            out IRandomAccessStreamReference reference)
        {
            lock (this._stateLock)
            {
                if (version == this._artworkVersion &&
                    this._artworkReference is { } current)
                {
                    reference = current;
                    return true;
                }

                reference = null!;
                return false;
            }
        }

        public void Hook()
        {
            this.Session.PlaybackInfoChanged += this.SessionOnPlaybackInfoChanged;
            this.Session.MediaPropertiesChanged += this.SessionOnMediaPropertiesChanged;
            this.Session.TimelinePropertiesChanged += this.SessionOnTimelinePropertiesChanged;
        }

        public void Unhook()
        {
            this.Session.PlaybackInfoChanged -= this.SessionOnPlaybackInfoChanged;
            this.Session.MediaPropertiesChanged -= this.SessionOnMediaPropertiesChanged;
            this.Session.TimelinePropertiesChanged -= this.SessionOnTimelinePropertiesChanged;
        }

        private void SessionOnPlaybackInfoChanged(
            GlobalSystemMediaTransportControlsSession sender,
            PlaybackInfoChangedEventArgs args)
        {
            this.Invalidate(SessionObservationChanges.Playback);
            owner.SignalStateChanged();
        }

        private void SessionOnMediaPropertiesChanged(
            GlobalSystemMediaTransportControlsSession sender,
            MediaPropertiesChangedEventArgs args)
        {
            this.Invalidate(SessionObservationChanges.MediaProperties);
            owner.SignalStateChanged();
        }

        private void SessionOnTimelinePropertiesChanged(
            GlobalSystemMediaTransportControlsSession sender,
            TimelinePropertiesChangedEventArgs args)
        {
            this.Invalidate(SessionObservationChanges.Timeline);
            owner.SignalStateChanged();
        }
    }
}