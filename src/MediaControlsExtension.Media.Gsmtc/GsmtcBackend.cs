// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using Microsoft.Extensions.Logging;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace JPSoftworks.MediaControlsExtension.Media.Gsmtc;

/// <summary>Discovers and controls Windows GSMTC sessions while keeping native objects inside this provider.</summary>
/// <remarks>Use a composite to obtain provider status, cross-provider pause outcomes, and operation timeouts.</remarks>
public sealed class GsmtcBackend : IMediaSourcePolicyBackend
{
    private const ulong MaxArtworkBytes = 32 * 1024 * 1024;
    private static readonly TimeSpan DisposalCleanupTimeout = TimeSpan.FromSeconds(5);

    [Flags]
    private enum ManagerChanges
    {
        None = 0,
        Sessions = 1 << 0,
        CurrentSession = 1 << 1,
        All = Sessions | CurrentSession,
    }

    [Flags]
    internal enum SessionObservationChanges
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

    private readonly record struct NativeCallTrace(
        long CallId,
        long StartedAt);

    private readonly record struct CurrentSessionResolution(
        bool IsConsistent,
        string Path,
        MediaBackendSessionId? CurrentSessionId);

    private readonly record struct ObservedSession(
        GlobalSystemMediaTransportControlsSession Session,
        string ApplicationId);

    private readonly record struct RecentSessionBinding(
        SessionBinding Binding,
        TimeSpan ExpiresAt);

    private readonly record struct MissingSessionRetention(
        long Version);

    private enum RetentionExpiryStatus
    {
        Inactive,
        Waiting,
        Expired,
    }

    private readonly GsmtcControlGate _controlGate;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly ILogger _logger;
    private readonly IGsmtcSourceActivator? _sourceActivator;
    private readonly GsmtcObservationGate _observationGate;
    private readonly List<RecentSessionBinding> _recentlyRemovedBindings = [];
    private readonly AdaptiveSessionRetentionPolicy _sessionRetentionPolicy = new();
    private readonly Channel<bool> _signals;
    private readonly Lock _stateLock = new();
    private readonly Dictionary<MediaBackendSessionId, SessionBinding> _bindings = [];
    private readonly List<Task<bool>> _sourcePolicyCleanups = [];
    private MediaBackendSourcePolicy _sourcePolicy = MediaBackendSourcePolicy.Empty;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private MediaBackendSessionId? _currentSessionId;
    private long _currentSessionSignalCount;
    private long _mediaSignalCount;
    private long _nextArtworkVersion;
    private long _nextBackendRevision;
    private long _nextNativeCallId;
    private long _nextSessionId;
    private long _playbackSignalCount;
    private long _sessionsSignalCount;
    private long _timelineSignalCount;
    private int _managerChanges = (int)ManagerChanges.All;
    private int _pendingSignals;
    private int _disposeState;
    private int _startState;

    /// <summary>Creates an unstarted GSMTC provider.</summary>
    /// <param name="logger">Non-null caller-owned diagnostic logger.</param>
    /// <param name="sourceActivator">Caller-owned activation adapter; null omits the ActivateSource capability.</param>
    public GsmtcBackend(ILogger<GsmtcBackend> logger, IGsmtcSourceActivator? sourceActivator = null)
    {
        this._logger = logger;
        this._sourceActivator = sourceActivator;
        this._controlGate = new(logger);
        this._observationGate = new(logger);
        this._signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false,
        });
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">The policy is null.</exception>
    /// <exception cref="ArgumentException">The revision decreases or changes exclusions without increasing.</exception>
    /// <exception cref="ObjectDisposedException">The provider is disposed.</exception>
    /// <remarks>Retired bindings reject new native uses; cleanup has a bounded wait and unfinished work stays tracked.</remarks>
    public async Task ApplySourcePolicyAsync(MediaBackendSourcePolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        cancellationToken.ThrowIfCancellationRequested();
        Task<bool>[] cleanups;
        lock (this._stateLock)
        {
            ObjectDisposedException.ThrowIf(this._disposeState != 0, this);
            if (policy.Revision < this._sourcePolicy.Revision)
            {
                throw new ArgumentException("Source policy revisions must not decrease.", nameof(policy));
            }

            if (policy.Revision == this._sourcePolicy.Revision &&
                !policy.ExcludedApplicationIds.SetEquals(this._sourcePolicy.ExcludedApplicationIds))
            {
                throw new ArgumentException("A source policy revision cannot change its exclusions.", nameof(policy));
            }

            this._sourcePolicy = policy;
            var excluded = this._bindings.Values.Concat(this._recentlyRemovedBindings.Select(static recent => recent.Binding))
                .Where(binding => policy.ExcludedApplicationIds.Contains(binding.ApplicationId))
                .Distinct<SessionBinding>(ReferenceEqualityComparer.Instance)
                .ToArray();
            foreach (var binding in excluded)
            {
                this._bindings.Remove(binding.Id);
                this._sourcePolicyCleanups.Add(binding.RetireAsync());
            }

            this._recentlyRemovedBindings.RemoveAll(recent => policy.ExcludedApplicationIds.Contains(recent.Binding.ApplicationId));
            if (this._currentSessionId is { } currentId && !this._bindings.ContainsKey(currentId))
            {
                this._currentSessionId = null;
            }

            cleanups = [.. this._sourcePolicyCleanups];
        }

        this.InvalidateManagerState(ManagerChanges.All, MediaBackendSignal.SessionsChanged | MediaBackendSignal.CurrentSessionChanged);
        await WaitForCleanupAsync(cleanups, DisposalCleanupTimeout, this._logger).ConfigureAwait(false);
        lock (this._stateLock)
        {
            this._sourcePolicyCleanups.RemoveAll(static task => task.IsCompletedSuccessfully && task.Result);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Startup was already attempted on this instance.</exception>
    /// <exception cref="ObjectDisposedException">The provider is disposed.</exception>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this._disposeState) != 0, this);
        if (Interlocked.CompareExchange(ref this._startState, 1, 0) != 0)
        {
            throw new InvalidOperationException("The GSMTC backend has already been started.");
        }

        var manager = await this._controlGate.RunAsync(
            static async () => await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(),
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

        this.SignalStateChanged(
            MediaBackendSignal.ObservationsChanged |
            MediaBackendSignal.SessionsChanged |
            MediaBackendSignal.CurrentSessionChanged);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MediaBackendSignal> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var _ in this._signals.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var signal = (MediaBackendSignal)Interlocked.Exchange(
                ref this._pendingSignals,
                (int)MediaBackendSignal.None);
            if (signal != MediaBackendSignal.None)
            {
                yield return signal;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>A successful read reports Connected even without sessions or when the control circuit is open.</remarks>
    public async Task<MediaBackendSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this._disposeState) != 0, this);
        this.LogAndResetSignalCounts();
        var managerChanges = (ManagerChanges)Interlocked.Exchange(
            ref this._managerChanges,
            (int)ManagerChanges.None);
        if (managerChanges != ManagerChanges.None)
        {
            try
            {
                await this.RefreshManagerStateAsync(
                    managerChanges,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Interlocked.Or(ref this._managerChanges, (int)managerChanges);
                throw;
            }
        }

        SessionBinding[] bindings;
        MediaBackendSessionId? currentSessionId;
        long sourcePolicyRevision;
        lock (this._stateLock)
        {
            bindings = [.. this._bindings.Values.OrderBy(static binding => binding.Id.Value)];
            currentSessionId = this._currentSessionId;
            sourcePolicyRevision = this._sourcePolicy.Revision;
        }

        var snapshots = ImmutableArray.CreateBuilder<MediaBackendSessionSnapshot>(bindings.Length);
        foreach (var binding in bindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (binding.IsMissing)
            {
                snapshots.Add(
                    (binding.LastSnapshot ?? CreateFallbackSnapshot(binding)) with
                    {
                        IsAvailable = false,
                    });
                continue;
            }

            var plan = binding.BeginObservation();
            if (plan.Changes == SessionObservationChanges.None)
            {
                snapshots.Add(plan.PreviousSnapshot!);
                continue;
            }

            try
            {
                var observed = await this._observationGate.RunAsync(
                    async () =>
                    {
                        using var nativeUse = binding.TryEnterNativeUse()
                            ?? throw new GsmtcSessionRetiredException();
                        return await ReadSessionAsync(binding, plan, nativeUse).ConfigureAwait(false);
                    },
                    $"ReadSession:{binding.ApplicationId}",
                    cancellationToken).ConfigureAwait(false);
                snapshots.Add(binding.CompleteObservation(observed));
            }
            catch (GsmtcSessionRetiredException)
            {
                binding.RestoreObservation(plan.Changes);
                snapshots.Add(binding.LastSnapshot ?? CreateFallbackSnapshot(binding));
            }
            catch (GsmtcObservationBlockedException)
            {
                binding.RestoreObservation(plan.Changes);
                snapshots.Add(binding.LastSnapshot ?? CreateFallbackSnapshot(binding));
            }
            catch (Exception ex) when (GsmtcErrors.IndicatesStaleSession(ex))
            {
                binding.RestoreObservation(plan.Changes);
                GsmtcLog.StaleSession(this._logger, binding.ApplicationId, "snapshot observation");
                snapshots.Add(binding.LastSnapshot ?? CreateFallbackSnapshot(binding));
                this.InvalidateManagerState(
                    ManagerChanges.All,
                    MediaBackendSignal.SessionsChanged |
                    MediaBackendSignal.CurrentSessionChanged);
            }
            catch (Exception ex)
            {
                binding.RestoreObservation(plan.Changes);
                GsmtcLog.SessionObservationFailed(this._logger, binding.ApplicationId, ex);
                snapshots.Add(binding.LastSnapshot ?? CreateFallbackSnapshot(binding));
            }
        }

        return new(
            Interlocked.Increment(ref this._nextBackendRevision),
            snapshots.MoveToImmutable(),
            currentSessionId is { } currentId ? [currentId] : [],
            this._controlGate.IsCircuitOpen
                ? MediaControlAvailability.CircuitOpen
                : MediaControlAvailability.Available)
        {
            SourcePolicyRevision = sourcePolicyRevision,
            Connection = MediaBackendConnectionState.Connected,
        };
    }

    /// <inheritdoc />
    public void InvalidateObservations(
        ImmutableArray<MediaBackendObservationRequest> requests)
    {
        lock (this._stateLock)
        {
            if (this._disposeState != 0)
            {
                return;
            }

            foreach (var request in requests)
            {
                if (this._bindings.TryGetValue(request.SessionId, out var binding))
                {
                    binding.Invalidate(MapObservationChanges(request.Changes));
                }
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>Direct secondary pauses do not populate PauseResults; use the composite for per-pause outcomes.</remarks>
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
                .Select(request => this._bindings.GetValueOrDefault(request.SessionId) is { } binding &&
                    binding.Generation == request.BindingGeneration ? binding : null)
                .Where(static binding => binding is { IsMissing: false })
                .Cast<SessionBinding>()
                .Distinct()
                .ToArray();
        }

        if (target is null ||
            target.IsMissing ||
            target.Generation != command.BindingGeneration)
        {
            return new(MediaBackendCommandStatus.SessionGone, "The target session was replaced or removed.");
        }

        try
        {
            if (command.Operation == MediaOperation.ActivateSource)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var sourceUse = target.TryEnterNativeUse() ?? throw new GsmtcSessionRetiredException();
                if (target.IsMissing)
                {
                    throw new GsmtcSessionRetiredException();
                }

                var activator = this._sourceActivator ?? throw new NotSupportedException("Source activation is not configured.");
                var activated = await activator.TryActivateAsync(
                    target.ApplicationId,
                    target.LastSnapshot?.MediaProperties.Title ?? string.Empty,
                    cancellationToken).ConfigureAwait(false);
                return activated
                    ? new(MediaBackendCommandStatus.Completed, null)
                    : new(MediaBackendCommandStatus.Failed, "The source application could not be activated.");
            }

            var ancillaryPausesProcessed = false;

            if (command.Operation is MediaOperation.Play or MediaOperation.Pause)
            {
                var result = await target.PlaybackController.ExecuteAsync(
                    command.Operation,
                    target.PlaybackObservations,
                    () => this.ReadPlaybackAsync(target, PlaybackReadLane.Transition),
                    (markSending, token) => RunControlAsync(
                        targetUse => GsmtcPlaybackController.SendRevalidatedAsync(
                            command.Operation,
                            target.PlaybackObservations,
                            target.PlaybackObservations.ReadCommand(() => this.ReadPlayback(target, targetUse, PlaybackReadLane.Command)),
                            nativeOperation => ExecuteOperationAsync(target.Session, nativeOperation, targetUse),
                            markSending),
                        markSending, token),
                    cancellationToken).ConfigureAwait(false);
                this.SignalStateChanged(MediaBackendSignal.ObservationsChanged);
                return result;
            }

            var accepted = await RunControlAsync(
                targetUse => ExecuteOperationAsync(target.Session, command.Operation, targetUse),
                static () => { }, cancellationToken).ConfigureAwait(false);
            if (accepted)
            {
                target.Invalidate(ChangesForOperation(command.Operation));
            }

            return accepted
                ? new(MediaBackendCommandStatus.Completed, null)
                : new(MediaBackendCommandStatus.Failed, "GSMTC rejected the requested operation.");

            async Task<T> RunControlAsync<T>(
                Func<GsmtcSessionNativeLifetime.NativeUse, Task<T>> execute, Action markSending, CancellationToken token)
            {
                var nativeOperationName = $"Command:{command.Operation}";
                var call = this.BeginNativeCall(target, nativeOperationName);
                var success = await this._controlGate.RunCommandAsync(
                    async () =>
                    {
                        using var targetUse = target.TryEnterNativeUse()
                                              ?? throw new GsmtcSessionRetiredException();
                        if (target.IsMissing)
                        {
                            throw new GsmtcSessionRetiredException();
                        }

                        var pauseTargets = ancillaryPausesProcessed ? [] : sessionsToPause;
                        ancillaryPausesProcessed = true;
                        foreach (var other in pauseTargets)
                        {
                            token.ThrowIfCancellationRequested();
                            using var otherUse = other.TryEnterNativeUse();
                            if (otherUse is null)
                            {
                                continue;
                            }

                            markSending();
                            try
                            {
                                if (await other.Session.TryPauseAsync())
                                {
                                    other.Invalidate(SessionObservationChanges.Playback);
                                }
                            }
                            catch (Exception ex)
                            {
                                GsmtcLog.PauseOtherSessionFailed(this._logger, other.ApplicationId, ex);
                            }
                        }

                        token.ThrowIfCancellationRequested();
                        return await execute(targetUse).ConfigureAwait(false);
                    },
                    command.Operation.ToString(),
                    token).ConfigureAwait(false);
                this.CompleteNativeCall(target, nativeOperationName, call);
                return success;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GsmtcSessionRetiredException ex)
        {
            return new(MediaBackendCommandStatus.SessionGone, ex.Message);
        }
        catch (GsmtcControlBusyException ex)
        {
            return new(MediaBackendCommandStatus.Unavailable, ex.Message);
        }
        catch (GsmtcControlCircuitOpenException ex)
        {
            return new(MediaBackendCommandStatus.Unavailable, ex.Message);
        }
        catch (Exception ex) when (command.Operation != MediaOperation.ActivateSource && GsmtcErrors.IndicatesStaleSession(ex))
        {
            this.InvalidateManagerState(
                ManagerChanges.All,
                MediaBackendSignal.SessionsChanged |
                MediaBackendSignal.CurrentSessionChanged);
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

    private Task<GsmtcPlaybackController.PlaybackObservation> ReadPlaybackAsync(
        SessionBinding binding,
        PlaybackReadLane lane)
    {
        using var nativeUse = binding.TryEnterNativeUse() ?? throw new GsmtcSessionRetiredException();
        if (binding.IsMissing)
        {
            throw new GsmtcSessionRetiredException();
        }

        return Task.FromResult(this.ReadPlayback(binding, nativeUse, lane));
    }

    private enum PlaybackReadLane { Snapshot, Command, Transition }

    private GsmtcPlaybackController.PlaybackObservation ReadPlayback(
        SessionBinding binding, GsmtcSessionNativeLifetime.NativeUse nativeUse, PlaybackReadLane lane)
    {
        var operationName = lane == PlaybackReadLane.Snapshot ? "GetPlaybackInfo" : "ConfirmPlayback";
        var call = this.BeginNativeCall(binding, operationName);
        var playbackInfo = binding.Session.GetPlaybackInfo();
        var controls = playbackInfo?.Controls;
        var observed = new GsmtcPlaybackController.PlaybackObservation(MapPlaybackState(playbackInfo?.PlaybackStatus), MapCapabilities(controls));

        switch (lane)
        {
            case PlaybackReadLane.Snapshot:
                nativeUse.CommitPlaybackObjects(playbackInfo, controls);
                break;
            case PlaybackReadLane.Command:
                nativeUse.CommitCommandPlaybackObjects(playbackInfo, controls);
                break;
            case PlaybackReadLane.Transition:
                nativeUse.CommitTransitionPlaybackObjects(playbackInfo, controls);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(lane), lane, null);
        }

        this.CompleteNativeCall(binding, operationName, call);
        return observed;
    }

    /// <inheritdoc />
    /// <remarks>Empty images and images larger than 32 MiB return null; successful content includes a hexadecimal SHA-256 hash.</remarks>
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
            var call = this.BeginNativeCall(binding, "ReadArtwork");
            var content = await this._observationGate.RunAsync(
                async () =>
                {
                    using var nativeUse = binding.TryEnterNativeUse()
                        ?? throw new GsmtcSessionRetiredException();
                    return await ReadArtworkAsync(reference).ConfigureAwait(false);
                },
                $"ReadArtwork:{binding.ApplicationId}",
                cancellationToken).ConfigureAwait(false);
            this.CompleteNativeCall(binding, "ReadArtwork", call);
            return content;
        }
        catch (GsmtcSessionRetiredException)
        {
            return null;
        }
        catch (GsmtcObservationBlockedException)
        {
            return null;
        }
    }

    /// <summary>Stops monitoring, retires native bindings, and attempts bounded native subscription cleanup.</summary>
    /// <returns>Completion of the cleanup attempt; native cleanup may continue after its five-second wait limit.</returns>
    /// <remarks>The owner must drain public calls first. Subsequent calls return immediately, without joining ongoing cleanup.</remarks>
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
        Task<bool>[] policyCleanups;
        lock (this._stateLock)
        {
            manager = this._manager;
            this._manager = null;
            bindings = this._bindings.Values
                .Concat(this._recentlyRemovedBindings.Select(static recent => recent.Binding))
                .Distinct<SessionBinding>(ReferenceEqualityComparer.Instance)
                .ToArray();
            this._bindings.Clear();
            this._recentlyRemovedBindings.Clear();
            this._currentSessionId = null;
            policyCleanups = [.. this._sourcePolicyCleanups];
        }

        var cleanupTasks = new List<Task<bool>>(
            bindings.Length + (manager is null ? 0 : 1));
        cleanupTasks.AddRange(policyCleanups);
        if (manager is not null)
        {
            cleanupTasks.Add(this.UnhookSessionManagerAsync(manager));
        }

        foreach (var binding in bindings)
        {
            cleanupTasks.Add(binding.RetireAsync());
        }

        try
        {
            await WaitForCleanupAsync(cleanupTasks, DisposalCleanupTimeout, this._logger).ConfigureAwait(false);
        }
        finally
        {
            this._disposeCts.Dispose();
        }
    }

    internal static async Task WaitForCleanupAsync(
        IReadOnlyCollection<Task<bool>> cleanupTasks,
        TimeSpan timeout,
        ILogger logger)
    {
        var cleanupTask = Task.WhenAll(cleanupTasks);
        try
        {
            var results = await cleanupTask.WaitAsync(timeout).ConfigureAwait(false);
            if (results.Any(static succeeded => !succeeded))
            {
                throw new InvalidOperationException("GSMTC cleanup failed; the provider cannot be restarted until extension reload.");
            }
        }
        catch (TimeoutException)
        {
            GsmtcLog.BackendCleanupTimedOut(
                logger,
                timeout,
                cleanupTasks.Count(static task => !task.IsCompleted),
                cleanupTasks.Count);
            _ = ObserveCleanupCompletionAsync(cleanupTask);
            throw;
        }
        catch (Exception ex)
        {
            GsmtcLog.BackendCleanupFailed(logger, ex);
            throw;
        }
    }

    private async Task<bool> UnhookSessionManagerAsync(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        try
        {
            await this._controlGate.RunCleanupAsync(
                () =>
                {
                    manager.SessionsChanged -= this.ManagerOnSessionsChanged;
                    manager.CurrentSessionChanged -= this.ManagerOnCurrentSessionChanged;
                    return Task.FromResult(true);
                },
                "DisposeSessionManager",
                CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            GsmtcLog.SessionManagerRetirementFailed(this._logger, ex);
            return false;
        }
    }

    private static async Task ObserveCleanupCompletionAsync(Task cleanupTask)
    {
        try
        {
            await cleanupTask.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static SessionObservationChanges ChangesForOperation(MediaOperation operation)
    {
        return operation is MediaOperation.SkipNext or MediaOperation.SkipPrevious
            ? SessionObservationChanges.All
            : SessionObservationChanges.Playback;
    }

    private static SessionObservationChanges MapObservationChanges(
        MediaBackendObservationChanges changes)
    {
        var result = SessionObservationChanges.None;
        if ((changes & MediaBackendObservationChanges.Playback) != 0)
        {
            result |= SessionObservationChanges.Playback;
        }

        if ((changes & MediaBackendObservationChanges.Timeline) != 0)
        {
            result |= SessionObservationChanges.Timeline;
        }

        return result;
    }

    private MediaCapabilities SourceCapabilities => this._sourceActivator is null ? MediaCapabilities.None : MediaCapabilities.ActivateSource;

    private MediaBackendSessionSnapshot CreateFallbackSnapshot(SessionBinding binding)
    {
        var source = new MediaSourceSnapshot { NativeApplication = new(binding.ApplicationId) };
        return new(
            binding.Id,
            binding.Generation,
            MediaPropertiesSnapshot.Empty(source),
            MediaTimelinePropertiesSnapshot.Empty,
            MediaPlaybackState.Unknown,
            this.SourceCapabilities);
    }

    private async Task<MediaBackendSessionSnapshot> ReadSessionAsync(
        SessionBinding binding,
        SessionObservationPlan plan,
        GsmtcSessionNativeLifetime.NativeUse nativeUse)
    {
        var previous = plan.PreviousSnapshot ?? this.CreateFallbackSnapshot(binding);

        var snapshot = await ReadSessionPartsAsync(
            previous, plan.Changes, binding.PlaybackObservations,
            () => this.ReadPlaybackAsync(binding, PlaybackReadLane.Snapshot), ReadTimeline, ReadMedia,
            binding.RestoreObservation,
            (part, exception) => GsmtcLog.SessionObservationPartFailed(this._logger, binding.ApplicationId, part, exception),
            this._disposeCts.Token).ConfigureAwait(false);
        return new(binding.Id, binding.Generation, snapshot.MediaProperties, snapshot.TimelineProperties,
            snapshot.PlaybackState, snapshot.Capabilities | this.SourceCapabilities);

        async Task<MediaPropertiesSnapshot> ReadMedia()
        {
            var call = this.BeginNativeCall(binding, "TryGetMediaPropertiesAsync");
            var properties = await binding.Session.TryGetMediaPropertiesAsync();
            var thumbnail = properties?.Thumbnail;
            var genres = properties?.Genres;
            var artwork = binding.UpdateArtworkReference(thumbnail);
            var source = previous.MediaProperties.Source;
            var result = properties is null ? MediaPropertiesSnapshot.Empty(source) : new(
                source, properties.Title ?? string.Empty, properties.Artist ?? string.Empty,
                properties.AlbumTitle ?? string.Empty, properties.AlbumArtist ?? string.Empty,
                properties.Subtitle ?? string.Empty, genres?.ToImmutableArray() ?? [],
                properties.TrackNumber, properties.AlbumTrackCount, MapContentType(properties.PlaybackType), artwork);
            nativeUse.CommitMediaObjects(properties, thumbnail, genres);
            this.CompleteNativeCall(binding, "TryGetMediaPropertiesAsync", call);
            return result;
        }

        MediaTimelinePropertiesSnapshot ReadTimeline()
        {
            var call = this.BeginNativeCall(binding, "GetTimelineProperties");
            var timeline = binding.Session.GetTimelineProperties();
            var result = new MediaTimelinePropertiesSnapshot(
                timeline.StartTime, timeline.EndTime, timeline.MinSeekTime, timeline.MaxSeekTime,
                timeline.Position, timeline.LastUpdatedTime);
            nativeUse.CommitTimelineObjects(timeline);
            this.CompleteNativeCall(binding, "GetTimelineProperties", call);
            return result;
        }
    }

    internal static async Task<MediaBackendSessionSnapshot> ReadSessionPartsAsync(
        MediaBackendSessionSnapshot previous, SessionObservationChanges changes, GsmtcPlaybackObservations observations,
        Func<Task<GsmtcPlaybackController.PlaybackObservation>> readPlayback, Func<MediaTimelinePropertiesSnapshot> readTimeline,
        Func<Task<MediaPropertiesSnapshot>> readMedia, Action<SessionObservationChanges> restore,
        Action<string, Exception> reportFailure, CancellationToken cancellationToken)
    {
        var snapshot = previous;
        observations.EnsureActive();
        if ((changes & SessionObservationChanges.Playback) != 0)
        {
            try
            {
                var playback = await observations.ReadSnapshotAsync(readPlayback, cancellationToken).ConfigureAwait(false);
                if (playback.Sequence != 0)
                {
                    snapshot = snapshot with { PlaybackState = playback.Value.State, Capabilities = playback.Value.Capabilities };
                }
            }
            catch (Exception ex) when (CanRetainObservationPart(ex))
            {
                reportFailure("playback information", ex);
            }
        }

        observations.EnsureActive();
        if (observations.IsSnapshotSuspended)
        {
            restore(changes & ~SessionObservationChanges.Playback);
            return snapshot;
        }

        if ((changes & SessionObservationChanges.Timeline) != 0)
        {
            try
            {
                snapshot = snapshot with { TimelineProperties = readTimeline() };
            }
            catch (Exception ex) when (CanRetainObservationPart(ex))
            {
                restore(SessionObservationChanges.Timeline);
                reportFailure("timeline", ex);
            }
        }

        observations.EnsureActive();
        if (observations.IsSnapshotSuspended)
        {
            restore(changes & SessionObservationChanges.MediaProperties);
            return snapshot;
        }

        if ((changes & SessionObservationChanges.MediaProperties) != 0)
        {
            try
            {
                snapshot = snapshot with { MediaProperties = await readMedia().ConfigureAwait(false) };
            }
            catch (Exception ex) when (CanRetainObservationPart(ex))
            {
                restore(SessionObservationChanges.MediaProperties);
                reportFailure("media properties", ex);
            }
        }

        return snapshot;
    }

    private NativeCallTrace BeginNativeCall(
        SessionBinding binding,
        string operation)
    {
        if (!this._logger.IsEnabled(LogLevel.Trace))
        {
            return default;
        }

        var trace = new NativeCallTrace(
            Interlocked.Increment(ref this._nextNativeCallId),
            Stopwatch.GetTimestamp());
        GsmtcLog.NativeCallStarting(
            this._logger,
            trace.CallId,
            operation,
            binding.Id.Value,
            binding.Generation,
            binding.ApplicationId);
        return trace;
    }

    private void CompleteNativeCall(
        SessionBinding binding,
        string operation,
        NativeCallTrace trace)
    {
        if (trace.CallId == 0 || !this._logger.IsEnabled(LogLevel.Trace))
        {
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(trace.StartedAt);
        GsmtcLog.NativeCallCompleted(
            this._logger,
            trace.CallId,
            operation,
            elapsed,
            binding.Id.Value,
            binding.Generation,
            binding.ApplicationId);
    }

    private NativeCallTrace BeginManagerCall(string operation)
    {
        if (!this._logger.IsEnabled(LogLevel.Trace))
        {
            return default;
        }

        var trace = new NativeCallTrace(
            Interlocked.Increment(ref this._nextNativeCallId),
            Stopwatch.GetTimestamp());
        GsmtcLog.ManagerCallStarting(
            this._logger,
            trace.CallId,
            operation);
        return trace;
    }

    private void CompleteManagerCall(
        string operation,
        NativeCallTrace trace)
    {
        if (trace.CallId == 0 || !this._logger.IsEnabled(LogLevel.Trace))
        {
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(trace.StartedAt);
        GsmtcLog.ManagerCallCompleted(
            this._logger,
            trace.CallId,
            operation,
            elapsed);
    }

    private static bool CanRetainObservationPart(Exception exception)
    {
        return exception is not OperationCanceledException and not GsmtcSessionRetiredException &&
               !GsmtcErrors.IndicatesStaleSession(exception);
    }

    private static MediaCapabilities MapCapabilities(
        GlobalSystemMediaTransportControlsSessionPlaybackControls? controls)
    {
        var capabilities = MediaCapabilities.None;
        if (controls?.IsPlayEnabled == true)
        {
            capabilities |= MediaCapabilities.Play;
        }

        if (controls?.IsPauseEnabled == true)
        {
            capabilities |= MediaCapabilities.Pause;
        }

        if (controls?.IsPlayPauseToggleEnabled == true)
        {
            capabilities |= MediaCapabilities.TogglePlayback;
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
        MediaOperation operation,
        GsmtcSessionNativeLifetime.NativeUse nativeUse)
    {
        return operation switch
        {
            MediaOperation.Play => await session.TryPlayAsync(),
            MediaOperation.Pause => await session.TryPauseAsync(),
            MediaOperation.TogglePlayback => await session.TryTogglePlayPauseAsync(),
            MediaOperation.Stop => await session.TryStopAsync(),
            MediaOperation.SkipNext => await session.TrySkipNextAsync(),
            MediaOperation.SkipPrevious => await session.TrySkipPreviousAsync(),
            MediaOperation.ToggleShuffle => await ToggleShuffleAsync(session, nativeUse),
            MediaOperation.ToggleRepeat => await ToggleRepeatAsync(session, nativeUse),
            _ => throw new NotSupportedException($"Media operation {operation} is not a primitive GSMTC operation."),
        };
    }

    private static async Task<bool> ToggleShuffleAsync(
        GlobalSystemMediaTransportControlsSession session,
        GsmtcSessionNativeLifetime.NativeUse nativeUse)
    {
        var playbackInfo = session.GetPlaybackInfo();
        var playbackControls = playbackInfo?.Controls;
        var isShuffleEnabled = playbackControls?.IsShuffleEnabled == true;
        var isShuffleActive = playbackInfo?.IsShuffleActive ?? false;
        nativeUse.CommitCommandPlaybackObjects(playbackInfo, playbackControls);
        if (!isShuffleEnabled)
        {
            return false;
        }

        return await session.TryChangeShuffleActiveAsync(!isShuffleActive);
    }

    private static async Task<bool> ToggleRepeatAsync(
        GlobalSystemMediaTransportControlsSession session,
        GsmtcSessionNativeLifetime.NativeUse nativeUse)
    {
        var playbackInfo = session.GetPlaybackInfo();
        var playbackControls = playbackInfo?.Controls;
        var isRepeatEnabled = playbackControls?.IsRepeatEnabled == true;
        var autoRepeatMode = isRepeatEnabled
            ? playbackInfo?.AutoRepeatMode
            : null;
        nativeUse.CommitCommandPlaybackObjects(playbackInfo, playbackControls);
        if (!isRepeatEnabled)
        {
            return false;
        }

        var nextMode = autoRepeatMode switch
        {
            MediaPlaybackAutoRepeatMode.None => MediaPlaybackAutoRepeatMode.Track,
            MediaPlaybackAutoRepeatMode.Track => MediaPlaybackAutoRepeatMode.List,
            MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.None,
            { } current => current,
            _ => MediaPlaybackAutoRepeatMode.None,
        };
        return await session.TryChangeAutoRepeatModeAsync(nextMode);
    }

    private async Task RefreshManagerStateAsync(
        ManagerChanges changes,
        CancellationToken cancellationToken)
    {
        if ((changes & ManagerChanges.Sessions) != 0)
        {
            await this.RefreshBindingsAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if ((changes & ManagerChanges.CurrentSession) != 0 &&
            !await this.TryRefreshCurrentSessionAsync(cancellationToken).ConfigureAwait(false))
        {
            await this.RefreshBindingsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> TryRefreshCurrentSessionAsync(
        CancellationToken cancellationToken)
    {
        GlobalSystemMediaTransportControlsSessionManager manager;
        SessionBinding[] bindings;
        MediaBackendSourcePolicy sourcePolicy;
        lock (this._stateLock)
        {
            manager = this._manager ?? throw new InvalidOperationException("The GSMTC backend is not started.");
            bindings = [.. this._bindings.Values];
            sourcePolicy = this._sourcePolicy;
        }

        var resolution = await this._controlGate.RunAsync(
            () =>
            {
                var call = this.BeginManagerCall("GetCurrentSession");
                var currentSession = manager.GetCurrentSession();
                this.CompleteManagerCall("GetCurrentSession", call);
                if (currentSession is null)
                {
                    return Task.FromResult(bindings.Length == 0
                        ? new CurrentSessionResolution(true, "fast-null-empty", null)
                        : new CurrentSessionResolution(
                            false,
                            "fallback-null-with-known-sessions",
                            null));
                }

                var referenceMatch = bindings.FirstOrDefault(
                    binding => ReferenceEquals(binding.Session, currentSession));
                return Task.FromResult(referenceMatch is null
                    ? new CurrentSessionResolution(
                        false,
                        "fallback-unmatched-reference",
                        null)
                    : new CurrentSessionResolution(
                        true,
                        "fast-reference",
                        referenceMatch.Id));
            },
            "RefreshCurrentSession",
            cancellationToken).ConfigureAwait(false);

        GsmtcLog.CurrentSessionReconciled(
            this._logger,
            resolution.Path,
            resolution.CurrentSessionId?.Value,
            bindings.Length);
        if (!resolution.IsConsistent)
        {
            return false;
        }

        lock (this._stateLock)
        {
            if (sourcePolicy != this._sourcePolicy)
            {
                return false;
            }

            this._currentSessionId = resolution.CurrentSessionId;
        }

        return true;
    }

    private async Task RefreshBindingsAsync(CancellationToken cancellationToken)
    {
        GlobalSystemMediaTransportControlsSessionManager manager;
        lock (this._stateLock)
        {
            manager = this._manager ?? throw new InvalidOperationException("The GSMTC backend is not started.");
        }

        var retentionsToSchedule = new List<(SessionBinding Binding, MissingSessionRetention Retention)>();
        var recentCleanupsToSchedule = new List<RecentSessionBinding>();
        await this._controlGate.RunAsync(
            () =>
            {
                SessionBinding[] existingBindings;
                RecentSessionBinding[] recentBindings;
                MediaBackendSourcePolicy sourcePolicy;
                lock (this._stateLock)
                {
                    existingBindings = [.. this._bindings.Values];
                    recentBindings = [.. this._recentlyRemovedBindings];
                    sourcePolicy = this._sourcePolicy;
                }

                var createdBindings = new List<SessionBinding>();
                var sessionsCall = this.BeginManagerCall("GetSessions");
                var sessions = manager.GetSessions() ?? [];
                this.CompleteManagerCall("GetSessions", sessionsCall);
                var currentCall = this.BeginManagerCall("GetCurrentSession");
                var currentSession = manager.GetCurrentSession();
                this.CompleteManagerCall("GetCurrentSession", currentCall);
                var now = GsmtcUnbiasedClock.GetTime();
                var observedSessions = new List<ObservedSession>(sessions.Count);
                var addedCount = 0;
                var retainedCount = 0;
                var reboundCount = 0;
                var removedCount = 0;
                foreach (var session in sessions)
                {
                    try
                    {
                        var applicationId = session.SourceAppUserModelId;
                        if (!sourcePolicy.ExcludedApplicationIds.Contains(applicationId))
                        {
                            observedSessions.Add(new(session, applicationId));
                        }
                    }
                    catch (Exception ex) when (GsmtcErrors.IndicatesStaleSession(ex))
                    {
                    }
                }

                var observedApplicationCounts = observedSessions
                    .GroupBy(static session => session.ApplicationId, StringComparer.Ordinal)
                    .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
                var availableActive = existingBindings.ToList();
                var availableRecent = recentBindings
                    .Where(binding => binding.ExpiresAt > now)
                    .ToList();
                var availableCandidates = availableActive
                    .Concat(availableRecent.Select(static binding => binding.Binding))
                    .ToList();
                var candidateApplicationCounts = availableCandidates
                    .GroupBy(static binding => binding.ApplicationId, StringComparer.Ordinal)
                    .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
                var nextBindings = new Dictionary<MediaBackendSessionId, SessionBinding>();

                foreach (var observed in observedSessions)
                {
                    var applicationIsUnambiguous =
                        observedApplicationCounts[observed.ApplicationId] == 1;
                    var existing = FindExistingBinding(
                        availableCandidates,
                        observed.Session,
                        observed.ApplicationId,
                        applicationIsUnambiguous);
                    SessionBinding binding;
                    if (existing is null)
                    {
                        addedCount++;
                        binding = new(
                            this,
                            new(Interlocked.Increment(ref this._nextSessionId)),
                            1,
                            observed.ApplicationId,
                            observed.Session);
                        createdBindings.Add(binding);
                        binding.Hook();
                    }
                    else
                    {
                        var wasActive = availableActive.Remove(existing);
                        var recentIndex = availableRecent.FindIndex(
                            candidate => ReferenceEquals(candidate.Binding, existing));
                        var wasRecent = recentIndex >= 0;
                        if (wasRecent)
                        {
                            availableRecent.RemoveAt(recentIndex);
                        }

                        availableCandidates.Remove(existing);
                        if (wasActive &&
                            !existing.IsMissing &&
                            ReferenceEquals(existing.Session, observed.Session))
                        {
                            retainedCount++;
                            binding = existing;
                        }
                        else
                        {
                            reboundCount++;
                            binding = new(
                                this,
                                existing.Id,
                                existing.Generation + 1,
                                observed.ApplicationId,
                                observed.Session);
                            createdBindings.Add(binding);
                            binding.SeedSnapshot(existing.LastSnapshot);
                            binding.Hook();
                            _ = existing.RetireInCurrentControlTurn();

                            var evidence = wasRecent
                                ? SessionRecreationEvidence.Weak
                                : SessionRecreationEvidence.Strong;
                            if (this._sessionRetentionPolicy.RecordRecreation(
                                observed.ApplicationId,
                                evidence,
                                existing.GetMissingDuration(now),
                                now))
                            {
                                var gracePeriod = this._sessionRetentionPolicy.GetGracePeriod(
                                    observed.ApplicationId,
                                    now,
                                    isUnambiguous: true);
                                GsmtcLog.SessionRecreationGraceIncreased(
                                    this._logger,
                                    observed.ApplicationId,
                                    gracePeriod);
                            }
                        }
                    }

                    nextBindings.Add(binding.Id, binding);
                }

                foreach (var missing in availableActive)
                {
                    if (!missing.IsMissing)
                    {
                        var isUnambiguous =
                            candidateApplicationCounts.GetValueOrDefault(missing.ApplicationId) == 1 &&
                            observedApplicationCounts.GetValueOrDefault(missing.ApplicationId) == 0;
                        var gracePeriod = this._sessionRetentionPolicy.GetGracePeriod(
                            missing.ApplicationId,
                            now,
                            isUnambiguous);
                        var retention = missing.BeginMissingRetention(
                            now,
                            now + gracePeriod);
                        _ = missing.RetireInCurrentControlTurn();
                        retentionsToSchedule.Add((missing, retention));
                        GsmtcLog.SessionRetentionStarted(
                            this._logger,
                            missing.ApplicationId,
                            gracePeriod);
                    }

                    if (!missing.IsRemovalDue)
                    {
                        retainedCount++;
                        nextBindings.Add(missing.Id, missing);
                        continue;
                    }

                    removedCount++;
                    var recent = new RecentSessionBinding(
                        missing,
                        now + this._sessionRetentionPolicy.RecentRemovalWindow);
                    availableRecent.Add(recent);
                    recentCleanupsToSchedule.Add(recent);
                    GsmtcLog.SessionRetentionExpired(this._logger, missing.ApplicationId);
                }

                var currentSessionId = FindCurrentSessionId(nextBindings.Values, currentSession);
                lock (this._stateLock)
                {
                    if (sourcePolicy != this._sourcePolicy || this._disposeState != 0)
                    {
                        foreach (var binding in createdBindings)
                        {
                            this._sourcePolicyCleanups.Add(binding.RetireInCurrentControlTurn());
                        }

                        return Task.FromResult(false);
                    }

                    this._bindings.Clear();
                    foreach (var (id, binding) in nextBindings)
                    {
                        this._bindings.Add(id, binding);
                    }

                    this._recentlyRemovedBindings.Clear();
                    this._recentlyRemovedBindings.AddRange(availableRecent);
                    this._currentSessionId = currentSessionId;
                }

                GsmtcLog.SessionReconciliationCompleted(
                    this._logger,
                    nextBindings.Count,
                    currentSessionId?.Value,
                    addedCount,
                    retainedCount,
                    reboundCount,
                    removedCount);

                return Task.FromResult(true);
            },
            "RefreshSessions",
            cancellationToken).ConfigureAwait(false);

        var disposeToken = this._disposeCts.Token;
        foreach (var (binding, retention) in retentionsToSchedule)
        {
            _ = this.ExpireRetentionAsync(binding, retention, disposeToken);
        }

        foreach (var recent in recentCleanupsToSchedule)
        {
            _ = this.ExpireRecentBindingAsync(recent, disposeToken);
        }
    }

    private static SessionBinding? FindExistingBinding(
        IReadOnlyCollection<SessionBinding> existingBindings,
        GlobalSystemMediaTransportControlsSession session,
        string applicationId,
        bool allowApplicationMatch)
    {
        var referenceMatch = existingBindings.FirstOrDefault(
            binding => ReferenceEquals(binding.Session, session));
        if (referenceMatch is not null)
        {
            return referenceMatch;
        }

        if (!allowApplicationMatch)
        {
            return null;
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

    private async Task ExpireRetentionAsync(
        SessionBinding binding,
        MissingSessionRetention retention,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var status = binding.TryExpireRetention(
                    retention.Version,
                    GsmtcUnbiasedClock.GetTime(),
                    out var remaining);
                if (status == RetentionExpiryStatus.Inactive)
                {
                    return;
                }

                if (status == RetentionExpiryStatus.Expired)
                {
                    break;
                }

                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
            }

            var isCurrentBinding = false;
            lock (this._stateLock)
            {
                isCurrentBinding = this._bindings.TryGetValue(binding.Id, out var current) &&
                                   ReferenceEquals(current, binding);
            }

            if (isCurrentBinding)
            {
                this.InvalidateManagerState(
                    ManagerChanges.All,
                    MediaBackendSignal.SessionsChanged |
                    MediaBackendSignal.CurrentSessionChanged);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ExpireRecentBindingAsync(
        RecentSessionBinding recent,
        CancellationToken cancellationToken)
    {
        try
        {
            var remaining = recent.ExpiresAt - GsmtcUnbiasedClock.GetTime();
            while (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                remaining = recent.ExpiresAt - GsmtcUnbiasedClock.GetTime();
            }

            var isCurrentTombstone = false;
            lock (this._stateLock)
            {
                isCurrentTombstone = this._recentlyRemovedBindings.Any(
                    candidate =>
                        ReferenceEquals(candidate.Binding, recent.Binding) &&
                        candidate.ExpiresAt == recent.ExpiresAt);
            }

            if (isCurrentTombstone)
            {
                this.InvalidateManagerState(
                    ManagerChanges.Sessions,
                    MediaBackendSignal.ObservationsChanged);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
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
        Interlocked.Increment(ref this._sessionsSignalCount);
        this.InvalidateManagerState(
            ManagerChanges.All,
            MediaBackendSignal.SessionsChanged |
            MediaBackendSignal.CurrentSessionChanged);
    }

    private void ManagerOnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        Interlocked.Increment(ref this._currentSessionSignalCount);
        this.InvalidateManagerState(
            ManagerChanges.CurrentSession,
            MediaBackendSignal.CurrentSessionChanged);
    }

    private void InvalidateManagerState(
        ManagerChanges changes,
        MediaBackendSignal signal)
    {
        Interlocked.Or(ref this._managerChanges, (int)changes);
        this.SignalStateChanged(signal);
    }

    private void SignalStateChanged(MediaBackendSignal signal)
    {
        if (Volatile.Read(ref this._disposeState) == 0)
        {
            Interlocked.Or(ref this._pendingSignals, (int)signal);
            this._signals.Writer.TryWrite(true);
        }
    }

    private void LogAndResetSignalCounts()
    {
        var playbackSignals = Interlocked.Exchange(ref this._playbackSignalCount, 0);
        var timelineSignals = Interlocked.Exchange(ref this._timelineSignalCount, 0);
        var mediaSignals = Interlocked.Exchange(ref this._mediaSignalCount, 0);
        var sessionsSignals = Interlocked.Exchange(ref this._sessionsSignalCount, 0);
        var currentSignals = Interlocked.Exchange(ref this._currentSessionSignalCount, 0);
        if (playbackSignals == 0 &&
            timelineSignals == 0 &&
            mediaSignals == 0 &&
            sessionsSignals == 0 &&
            currentSignals == 0)
        {
            return;
        }

        GsmtcLog.NativeSignalsDrained(
            this._logger,
            playbackSignals,
            timelineSignals,
            mediaSignals,
            sessionsSignals,
            currentSignals);
    }

    private sealed class SessionBinding(
        GsmtcBackend owner,
        MediaBackendSessionId id,
        long generation,
        string applicationId,
        GlobalSystemMediaTransportControlsSession session)
    {
        private readonly GsmtcSessionNativeLifetime _nativeLifetime = new();
        private readonly Lock _stateLock = new();
        private GsmtcPlaybackObservations? _playbackObservations;
        private IRandomAccessStreamReference? _artworkReference;
        private bool _artworkChanged = true;
        private int _isHooked;
        private bool _isMissing;
        private TimeSpan _missingSince;
        private long _artworkVersion;
        private MediaBackendSessionSnapshot? _lastSnapshot;
        private SessionObservationChanges _pendingChanges = SessionObservationChanges.Timeline | SessionObservationChanges.MediaProperties;
        private bool _removalDue;
        private TimeSpan _retentionDeadline;
        private long _retentionVersion;

        public MediaBackendSessionId Id { get; } = id;

        public long Generation { get; } = generation;

        public string ApplicationId { get; } = applicationId;

        public GlobalSystemMediaTransportControlsSession Session { get; } = session;

        public GsmtcPlaybackController PlaybackController { get; } = new();

        public GsmtcPlaybackObservations PlaybackObservations
        {
            get
            {
                lock (this._stateLock)
                {
                    return this._playbackObservations ??= new(this._stateLock,
                        () => owner.SignalStateChanged(MediaBackendSignal.ObservationsChanged),
                        owner._logger, $"GetPlaybackInfo:{this.ApplicationId}");
                }
            }
        }

        public GsmtcSessionNativeLifetime.NativeUse? TryEnterNativeUse()
        {
            lock (owner._stateLock)
            {
                return !owner._sourcePolicy.ExcludedApplicationIds.Contains(this.ApplicationId) &&
                    owner._bindings.TryGetValue(this.Id, out var current) && ReferenceEquals(current, this)
                        ? this._nativeLifetime.TryEnter()
                        : null;
            }
        }

        public Task<bool> RetireAsync()
        {
            this.PlaybackObservations.Retire();
            return this._nativeLifetime.RetireAsync(this.UnhookAfterNativeUsesAsync);
        }

        public Task<bool> RetireInCurrentControlTurn()
        {
            this.PlaybackObservations.Retire();
            return this._nativeLifetime.RetireInCurrentTurn(
                this.TryUnhookCore,
                this.UnhookAfterNativeUsesAsync);
        }

        public bool IsMissing
        {
            get
            {
                lock (this._stateLock)
                {
                    return this._isMissing;
                }
            }
        }

        public bool IsRemovalDue
        {
            get
            {
                lock (this._stateLock)
                {
                    return this._removalDue;
                }
            }
        }

        public MediaBackendSessionSnapshot? LastSnapshot
        {
            get
            {
                lock (this._stateLock)
                {
                    return this._lastSnapshot is { } snapshot ? this.PlaybackObservations.Merge(snapshot, owner.SourceCapabilities) : null;
                }
            }
        }

        public SessionObservationPlan BeginObservation()
        {
            lock (this._stateLock)
            {
                if (this.PlaybackObservations.IsSnapshotSuspended)
                {
                    return new(this.LastSnapshot ?? owner.CreateFallbackSnapshot(this), SessionObservationChanges.None);
                }

                var plan = new SessionObservationPlan(
                    this.LastSnapshot,
                    this._pendingChanges |
                    (this.PlaybackObservations.NeedsSnapshotRead ? SessionObservationChanges.Playback : SessionObservationChanges.None));
                this._pendingChanges = SessionObservationChanges.None;
                return plan;
            }
        }

        public MediaBackendSessionSnapshot CompleteObservation(MediaBackendSessionSnapshot snapshot)
        {
            lock (this._stateLock)
            {
                this._lastSnapshot = this.PlaybackObservations.Merge(snapshot, owner.SourceCapabilities);
                return this._lastSnapshot;
            }
        }

        public void RestoreObservation(SessionObservationChanges changes)
        {
            this.Invalidate(changes & ~SessionObservationChanges.Playback);
        }

        public void Invalidate(SessionObservationChanges changes)
        {
            lock (this._stateLock)
            {
                this._pendingChanges |= changes & ~SessionObservationChanges.Playback;
                if ((changes & SessionObservationChanges.Playback) != 0)
                {
                    this.PlaybackObservations.Invalidate();
                }
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

        public MissingSessionRetention BeginMissingRetention(
            TimeSpan missingSince,
            TimeSpan deadline)
        {
            lock (this._stateLock)
            {
                if (this._isMissing)
                {
                    throw new InvalidOperationException("The session is already being retained as missing.");
                }

                this._isMissing = true;
                this.PlaybackObservations.Retire();
                this._missingSince = missingSince;
                this._removalDue = false;
                this._retentionDeadline = deadline;
                this._retentionVersion++;
                return new(this._retentionVersion);
            }
        }

        public TimeSpan? GetMissingDuration(TimeSpan now)
        {
            lock (this._stateLock)
            {
                if (!this._isMissing)
                {
                    return null;
                }

                var duration = now - this._missingSince;
                return duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
            }
        }

        public RetentionExpiryStatus TryExpireRetention(
            long version,
            TimeSpan now,
            out TimeSpan remaining)
        {
            lock (this._stateLock)
            {
                if (!this._isMissing || version != this._retentionVersion)
                {
                    remaining = TimeSpan.Zero;
                    return RetentionExpiryStatus.Inactive;
                }

                remaining = this._retentionDeadline - now;
                if (remaining > TimeSpan.Zero)
                {
                    return RetentionExpiryStatus.Waiting;
                }

                this._removalDue = true;
                remaining = TimeSpan.Zero;
                return RetentionExpiryStatus.Expired;
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
                if (!this._isMissing &&
                    version == this._artworkVersion &&
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
            if (Interlocked.Exchange(ref this._isHooked, 1) != 0)
            {
                return;
            }

            this.Session.PlaybackInfoChanged += this.SessionOnPlaybackInfoChanged;
            this.Session.MediaPropertiesChanged += this.SessionOnMediaPropertiesChanged;
            this.Session.TimelinePropertiesChanged += this.SessionOnTimelinePropertiesChanged;
        }

        private async Task<bool> UnhookAfterNativeUsesAsync()
        {
            try
            {
                return await owner._controlGate.RunCleanupAsync(
                    () => Task.FromResult(this.TryUnhookCore()),
                    $"RetireSession:{this.ApplicationId}",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.LogRetirementFailure(ex);
                return false;
            }
        }

        private bool TryUnhookCore()
        {
            try
            {
                this.UnhookCore();
                return true;
            }
            catch (Exception ex)
            {
                this.LogRetirementFailure(ex);
                return false;
            }
        }

        private void LogRetirementFailure(Exception exception)
        {
            try
            {
                GsmtcLog.SessionRetirementFailed(
                    owner._logger,
                    this.ApplicationId,
                    exception);
            }
            catch
            {
                // Retirement must not fault a forgotten background task if
                // the logging pipeline is already unavailable.
            }
        }

        private void UnhookCore()
        {
            if (Interlocked.Exchange(ref this._isHooked, 0) != 0)
            {
                this.Session.PlaybackInfoChanged -= this.SessionOnPlaybackInfoChanged;
                this.Session.MediaPropertiesChanged -= this.SessionOnMediaPropertiesChanged;
                this.Session.TimelinePropertiesChanged -= this.SessionOnTimelinePropertiesChanged;
            }
        }

        private void SessionOnPlaybackInfoChanged(
            GlobalSystemMediaTransportControlsSession sender,
            PlaybackInfoChangedEventArgs args)
        {
            this.Invalidate(SessionObservationChanges.Playback);
            Interlocked.Increment(ref owner._playbackSignalCount);
            owner.SignalStateChanged(MediaBackendSignal.ObservationsChanged);
        }

        private void SessionOnMediaPropertiesChanged(
            GlobalSystemMediaTransportControlsSession sender,
            MediaPropertiesChangedEventArgs args)
        {
            this.Invalidate(SessionObservationChanges.MediaProperties);
            Interlocked.Increment(ref owner._mediaSignalCount);
            owner.SignalStateChanged(MediaBackendSignal.ObservationsChanged);
        }

        private void SessionOnTimelinePropertiesChanged(
            GlobalSystemMediaTransportControlsSession sender,
            TimelinePropertiesChangedEventArgs args)
        {
            this.Invalidate(SessionObservationChanges.Timeline);
            Interlocked.Increment(ref owner._timelineSignalCount);
            owner.SignalStateChanged(MediaBackendSignal.ObservationsChanged);
        }
    }
}