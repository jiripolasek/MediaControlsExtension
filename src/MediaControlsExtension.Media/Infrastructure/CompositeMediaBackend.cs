// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.MediaControlsExtension.Media.Infrastructure;

/// <summary>Combines independently managed providers behind one stable backend.</summary>
/// <remarks>
/// Owns created providers but not the logging factory. Session IDs are unique for this composite lifetime;
/// re-enabling a provider gives its sessions fresh IDs. State access and selection changes are thread-safe.
/// </remarks>
public sealed class CompositeMediaBackend : IMediaBackend
{
    private readonly Lock _stateLock = new();
    private readonly ImmutableArray<ProviderEntry> _providers;
    private readonly Dictionary<MediaBackendSessionId, SessionRoute> _routes = [];
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly TimeSpan _operationTimeout;
    private readonly Channel<bool> _signals = CreateSignalChannel();
    private bool _started;
    private bool _disposed;
    private Task? _disposeTask;
    private long _nextSessionId;
    private long _nextArtworkVersion;
    private long _revision;
    private long _nextSourcePolicyRevision;
    private int _pendingSignals;

    /// <summary>Captures registrations and initial enablement without creating provider instances.</summary>
    /// <param name="registry">Configured registry; later registrations do not affect this composite.</param>
    /// <param name="enabledBackendIds">Exact enabled IDs; null uses registration defaults, while an empty list disables all.</param>
    /// <param name="loggerFactory">Caller-owned logging factory, or null to disable logging.</param>
    /// <param name="operationTimeout">Per-call command and artwork timeout; null uses ten seconds.</param>
    /// <exception cref="ArgumentNullException">The registry is null.</exception>
    /// <exception cref="ArgumentException">An enabled ID or source-claim target is not registered.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is not positive or exceeds 4,294,967,294 milliseconds.</exception>
    /// <exception cref="InvalidOperationException">Multiple enabled providers claim the same backend and application.</exception>
    public CompositeMediaBackend(
        MediaBackendRegistry registry,
        IEnumerable<string>? enabledBackendIds = null,
        ILoggerFactory? loggerFactory = null,
        TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        this._loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        this._logger = this._loggerFactory.CreateLogger<CompositeMediaBackend>();
        this._operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(10);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(this._operationTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(this._operationTimeout, TimeSpan.FromMilliseconds(uint.MaxValue - 1));
        var registrations = registry.Registrations;
        var enabled = enabledBackendIds?.ToHashSet(StringComparer.Ordinal)
            ?? registrations.Where(static registration => registration.EnabledByDefault)
                .Select(static registration => registration.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in enabled)
        {
            if (!registrations.Any(registration => string.Equals(registration.Id, id, StringComparison.Ordinal)))
            {
                throw new ArgumentException($"Unknown media backend '{id}'.", nameof(enabledBackendIds));
            }
        }

        this._providers = [.. registrations.Select(registration => new ProviderEntry(registration, enabled.Contains(registration.Id)))];
        foreach (var claim in registrations.SelectMany(static registration => registration.ReplacesSources))
        {
            if (!registrations.Any(registration => registration.Id == claim.BackendId))
            {
                throw new ArgumentException($"A source claim refers to unknown backend '{claim.BackendId}'.", nameof(registry));
            }
        }

        this.ValidateSourceOwnersUnderLock(null, false);
        this.UpdateSourcePoliciesUnderLock();
    }

    /// <summary>Gets current states for all registrations in registry order, including disabled or faulted providers.</summary>
    public ImmutableArray<MediaBackendState> Backends
    {
        get
        {
            lock (this._stateLock)
            {
                return this.CreateBackendStatesUnderLock();
            }
        }
    }

    /// <summary>Starts discovery independently for each enabled provider.</summary>
    /// <param name="cancellationToken">Checked before scheduling; later cancellation does not stop provider startup.</param>
    /// <returns>A completed task once startup is scheduled; initial provider snapshots arrive through monitoring.</returns>
    /// <exception cref="OperationCanceledException">The token was already canceled.</exception>
    /// <exception cref="ObjectDisposedException">The composite is disposed.</exception>
    /// <remarks>Repeated calls are harmless. Provider failures are reported through Backends.</remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this._stateLock)
        {
            ObjectDisposedException.ThrowIf(this._disposed, this);
            if (this._started)
            {
                return Task.CompletedTask;
            }

            this._started = true;
            foreach (var entry in this._providers)
            {
                this.QueueTransitionUnderLock(entry);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Updates desired enablement and waits for the affected lifecycle and source-policy transitions.</summary>
    /// <param name="backendId">Case-sensitive registration ID.</param>
    /// <param name="enabled">Desired selection; enabling a faulted provider also requests a retry.</param>
    /// <param name="cancellationToken">Checked before acceptance; afterward, cancellation only stops this caller's wait.</param>
    /// <returns>Completion of affected transitions; inspect Backends for provider failures.</returns>
    /// <exception cref="ArgumentException">The ID is not registered.</exception>
    /// <exception cref="InvalidOperationException">Enabling would create conflicting source claims.</exception>
    /// <exception cref="OperationCanceledException">The request or its wait was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The composite is disposed.</exception>
    /// <remarks>
    /// Before startup this only updates selection. Disabling immediately withdraws sessions, then drains and disposes the instance.
    /// Concurrent requests converge on the latest selection. Disposal failure blocks replacement until this composite is replaced.
    /// </remarks>
    public Task SetEnabledAsync(string backendId, bool enabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task transition;
        ProviderRun? retired = null;
        lock (this._stateLock)
        {
            ObjectDisposedException.ThrowIf(this._disposed, this);
            var entry = this._providers.FirstOrDefault(entry => string.Equals(entry.Registration.Id, backendId, StringComparison.Ordinal))
                ?? throw new ArgumentException($"Unknown media backend '{backendId}'.", nameof(backendId));
            var changed = entry.Enabled != enabled;
            var retry = entry.Status == MediaBackendLifecycleStatus.Faulted;
            this.ValidateSourceOwnersUnderLock(entry, enabled);
            entry.Enabled = enabled;
            var affected = this.UpdateSourcePoliciesUnderLock();
            if (changed)
            {
                this._revision++;
                this.Signal(MediaBackendSignal.SessionsChanged | MediaBackendSignal.CurrentSessionChanged | MediaBackendSignal.BackendsChanged);
            }

            if (!enabled && entry.Run is { } run)
            {
                this.RetireUnderLock(run);
                retired = run;
                this.SetLifecycleStateUnderLock(entry,
                    run.DisposalFailed ? MediaBackendLifecycleStatus.Faulted : MediaBackendLifecycleStatus.Stopping, entry.Error);
            }

            if (this._started)
            {
                if (changed || retry)
                {
                    affected.Add(entry);
                }

                foreach (var affectedEntry in affected)
                {
                    this.QueueTransitionUnderLock(affectedEntry);
                }

                affected.Add(entry);
                foreach (var claim in entry.Registration.ReplacesSources)
                {
                    affected.Add(this._providers.Single(provider => provider.Registration.Id == claim.BackendId));
                }

                transition = Task.WhenAll(affected.Select(static provider => provider.Transition));
            }
            else
            {
                transition = entry.Transition;
            }
        }

        if (retired is not null)
        {
            _ = this.CancelRunAsync(retired);
        }

        return transition.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MediaBackendSignal> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var _ in this._signals.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var signal = (MediaBackendSignal)Interlocked.Exchange(ref this._pendingSignals, 0);
            if (signal != MediaBackendSignal.None)
            {
                yield return signal;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>Returns cached state immediately and schedules dirty provider reads; it does not wait for fresh observations.</remarks>
    public Task<MediaBackendSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this._stateLock)
        {
            ObjectDisposedException.ThrowIf(this._disposed, this);
            foreach (var entry in this._providers)
            {
                if (entry.Run is { Started: true, Retired: false, TerminalFault: false, Dirty: true, Reading: false, ApplyingPolicy: false } run &&
                    run.AppliedPolicyRevision == entry.SourcePolicy.Revision)
                {
                    this.StartRefreshUnderLock(run);
                }
            }

            var sessions = this._providers.SelectMany(static entry => entry.Run is { Retired: false } run
                ? run.Snapshot?.Sessions.Select(local => Present(run.Routes[local.Id])) ?? []
                : []).ToImmutableArray();
            var currentHints = this._providers.SelectMany(static entry => entry.Run is { Retired: false, Snapshot: { } snapshot } run
                ? snapshot.CurrentSessionHints.Select(id => run.Routes.GetValueOrDefault(id))
                    .Where(static route => route is not null && IsAvailable(route))
                    .Select(static route => route!.Id)
                : []).ToImmutableArray();
            var available = this._providers.Any(static entry => entry.Run is { Retired: false, ReadFailed: false, TerminalFault: false } run &&
                run.Snapshot?.Availability == MediaControlAvailability.Available);
            var backends = this.CreateBackendStatesUnderLock();
            return Task.FromResult(new MediaBackendSnapshot(this._revision, sessions, currentHints,
                available || this._providers.All(static entry => !entry.Enabled)
                    ? MediaControlAvailability.Available
                    : MediaControlAvailability.Unavailable)
            {
                Backends = backends,
                Connection = AggregateConnectionState(backends),
            });
        }
    }

    /// <inheritdoc />
    public void InvalidateObservations(ImmutableArray<MediaBackendObservationRequest> requests)
    {
        foreach (var request in requests)
        {
            using var use = this.TryAcquire(request.SessionId, null, out _);
            if (use is null)
            {
                continue;
            }

            try
            {
                use.Run.Backend!.InvalidateObservations([new(use.Local.Id, request.Changes)]);
                lock (this._stateLock)
                {
                    use.Run.Dirty = true;
                }

                this.Signal(MediaBackendSignal.ObservationsChanged);
            }
            catch (Exception ex)
            {
                MediaBackendLog.ProviderFailed(this._logger, use.Run.Entry.Registration.Id, "invalidate observations", ex);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// For Play, pauses distinct secondary bindings first, excluding the primary; failures do not suppress the primary operation.
    /// Each distinct secondary target receives a result, including missing or unsupported targets, if the pause phase completes.
    /// Pauses are sequential per provider and parallel across providers. Timeouts return Unavailable while underlying work drains.
    /// </remarks>
    public async Task<MediaBackendCommandResult> ExecuteAsync(MediaBackendCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = this.TryAcquire(command.SessionId, command.BindingGeneration, out var failure);
        if (target is null)
        {
            return new(failure, "The target session is unavailable or has been replaced.");
        }

        var transferred = false;
        ImmutableArray<MediaBackendPauseResult> pauseResults = [];
        using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, target.Run.Cancellation.Token);
        try
        {
            if (command.Operation == MediaOperation.Play)
            {
                pauseResults = await this.PauseOthersAsync(command, commandCancellation.Token).ConfigureAwait(false);
            }

            commandCancellation.Token.ThrowIfCancellationRequested();
            transferred = true;
            var result = await this.ExecuteWithUseAsync(target, command.Operation, cancellationToken).ConfigureAwait(false);
            return result with { PauseResults = pauseResults };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(MediaBackendCommandStatus.Unavailable, "The target provider was disabled.") { PauseResults = pauseResults };
        }
        finally
        {
            if (!transferred)
            {
                target.Dispose();
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>Returns null on provider failure, timeout, or a key becoming obsolete before completion.</remarks>
    public async ValueTask<MediaArtworkContent?> GetArtworkAsync(MediaArtworkKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SessionUse? use;
        lock (this._stateLock)
        {
            var id = new MediaBackendSessionId(key.SessionId.Value);
            use = this._routes.TryGetValue(id, out var route) && route.Artwork == key
                ? this.TryAcquire(id, null, out _)
                : null;
        }

        if (use is null || use.Local.MediaProperties.Artwork is not { } localKey)
        {
            use?.Dispose();
            return null;
        }

        var cancellation = this.CreateOperationCancellation(use, cancellationToken);
        var token = cancellation.Token;
        var operation = this.ReadArtworkCoreAsync(use, localKey, cancellation);
        try
        {
            var content = await operation.WaitAsync(token).ConfigureAwait(false);
            lock (this._stateLock)
            {
                return this._routes.TryGetValue(new(key.SessionId.Value), out var route) && route.Artwork == key && IsAvailable(route)
                    ? content
                    : null;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Withdraws all providers, cancels and drains their work, then disposes them and completes monitoring.</summary>
    /// <returns>The shared disposal task; provider cleanup failures are retained in Backends and logged.</returns>
    /// <remarks>A provider operation that ignores cancellation can delay completion until that operation returns.</remarks>
    public ValueTask DisposeAsync()
    {
        lock (this._stateLock)
        {
            if (this._disposeTask is not null)
            {
                return new(this._disposeTask);
            }

            this._disposed = true;
            foreach (var entry in this._providers)
            {
                entry.Enabled = false;
                if (entry.Run is { } run)
                {
                    this.RetireUnderLock(run);
                    _ = this.CancelRunAsync(run);
                }

                this.QueueTransitionUnderLock(entry);
            }

            this._disposeTask = this.FinishDisposalAsync();
            return new(this._disposeTask);
        }
    }

    private static Channel<bool> CreateSignalChannel() => Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = false,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest,
        AllowSynchronousContinuations = false,
    });

    private ImmutableArray<MediaBackendState> CreateBackendStatesUnderLock() =>
        [.. this._providers.Select(static entry => new MediaBackendState(entry.Registration.Id, entry.Enabled, entry.Status, entry.Error)
        {
            DisplayName = entry.Registration.DisplayName,
            Connection = GetConnectionState(entry),
            AvailableSessionCount = entry.Run?.Routes.Values.Count(IsAvailable) ?? 0,
        })];

    private static MediaBackendConnectionState GetConnectionState(ProviderEntry entry)
    {
        if (entry.Run is { } run)
        {
            if (run.DisposalFailed || (!run.Retired && (run.ReadFailed || run.TerminalFault)))
            {
                return new(MediaConnectionStatus.Unknown, entry.Error)
                {
                    Connections = [.. (run.Snapshot?.Connection.Connections ?? []).Select(connection => connection with
                    {
                        Status = MediaConnectionStatus.Unknown,
                        DiagnosticMessage = entry.Error,
                    })],
                };
            }

            if (!run.Retired && run.Snapshot is { } snapshot)
            {
                return snapshot.Connection;
            }
        }

        return entry.Status == MediaBackendLifecycleStatus.Starting
            ? MediaBackendConnectionState.Connecting
            : MediaBackendConnectionState.Disconnected;
    }

    private static MediaBackendConnectionState AggregateConnectionState(ImmutableArray<MediaBackendState> backends)
    {
        var enabled = backends.Where(static backend => backend.IsEnabled).ToArray();
        if (enabled.Any(static backend => backend.Connection.Status == MediaConnectionStatus.Connected))
        {
            return MediaBackendConnectionState.Connected;
        }

        if (enabled.Any(static backend => backend.Connection.Status == MediaConnectionStatus.Connecting))
        {
            return MediaBackendConnectionState.Connecting;
        }

        return enabled.Any(static backend => backend.Connection.Status == MediaConnectionStatus.Unknown)
            ? MediaBackendConnectionState.Unknown
            : MediaBackendConnectionState.Disconnected;
    }

    private void SetLifecycleStateUnderLock(ProviderEntry entry, MediaBackendLifecycleStatus status, string? error)
    {
        if (entry.Status == status && entry.Error == error)
        {
            return;
        }

        entry.Status = status;
        entry.Error = error;
        this._revision++;
        this.Signal(MediaBackendSignal.BackendsChanged);
    }

    private Task QueueTransitionUnderLock(ProviderEntry entry)
    {
        if (this._started && entry.Enabled && entry.Run is null && entry.Status == MediaBackendLifecycleStatus.Disabled)
        {
            this.SetLifecycleStateUnderLock(entry, MediaBackendLifecycleStatus.Starting, null);
        }

        var previous = entry.Transition;
        entry.Transition = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            await this.ReconcileAsync(entry).ConfigureAwait(false);
        });
        return entry.Transition;
    }

    private void ValidateSourceOwnersUnderLock(ProviderEntry? changedEntry, bool enabled)
    {
        var owners = new HashSet<MediaBackendSourceClaim>();
        foreach (var entry in this._providers)
        {
            if (!(entry == changedEntry ? enabled : entry.Enabled))
            {
                continue;
            }

            foreach (var claim in entry.Registration.ReplacesSources)
            {
                if (!owners.Add(claim))
                {
                    throw new InvalidOperationException($"Multiple enabled backends replace '{claim.ApplicationId}' from '{claim.BackendId}'.");
                }
            }
        }
    }

    private HashSet<ProviderEntry> UpdateSourcePoliciesUnderLock()
    {
        var affected = new HashSet<ProviderEntry>();
        var claims = this._providers.Where(static entry => entry.Enabled)
            .SelectMany(static entry => entry.Registration.ReplacesSources).ToArray();
        foreach (var entry in this._providers)
        {
            var excluded = claims.Where(claim => claim.BackendId == entry.Registration.Id)
                .Select(static claim => claim.ApplicationId).ToImmutableHashSet(StringComparer.Ordinal);
            if (entry.SourcePolicy.ExcludedApplicationIds.SetEquals(excluded))
            {
                continue;
            }

            entry.SourcePolicy = new(++this._nextSourcePolicyRevision, excluded);
            affected.Add(entry);
            if (entry.Run is { Retired: false } run)
            {
                foreach (var route in run.Routes.Values.Where(route => IsExcluded(route.Local, excluded)).ToArray())
                {
                    this._routes.Remove(route.Id);
                    run.Routes.Remove(route.Local.Id);
                }

                if (run.Snapshot is { } snapshot)
                {
                    run.Snapshot = snapshot with
                    {
                        Sessions = [.. snapshot.Sessions.Where(session => run.Routes.ContainsKey(session.Id))],
                        CurrentSessionHints = [.. snapshot.CurrentSessionHints.Where(run.Routes.ContainsKey)],
                    };
                }

                run.Dirty = true;
            }

            this._revision++;
            this.Signal(MediaBackendSignal.SessionsChanged | MediaBackendSignal.CurrentSessionChanged | MediaBackendSignal.BackendsChanged);
        }

        return affected;
    }

    private async Task ReconcileAsync(ProviderEntry entry)
    {
        while (true)
        {
            ProviderRun run;
            bool create;
            var updatePolicy = false;
            lock (this._stateLock)
            {
                if (entry.Run is { } existing)
                {
                    if (entry.Enabled && !existing.Retired && !existing.TerminalFault && !existing.ReadFailed)
                    {
                        if (existing.AppliedPolicyRevision == entry.SourcePolicy.Revision)
                        {
                            return;
                        }

                        updatePolicy = true;
                    }

                    run = existing;
                    create = false;
                    if (!updatePolicy)
                    {
                        this.RetireUnderLock(run);
                        this.SetLifecycleStateUnderLock(entry, MediaBackendLifecycleStatus.Stopping, entry.Error);
                    }
                }
                else
                {
                    if (!entry.Enabled || this._disposed)
                    {
                        this.SetLifecycleStateUnderLock(entry, MediaBackendLifecycleStatus.Disabled, null);
                        return;
                    }

                    run = new ProviderRun(entry);
                    entry.Run = run;
                    this.SetLifecycleStateUnderLock(entry, MediaBackendLifecycleStatus.Starting, null);
                    create = true;
                }
            }

            if (updatePolicy)
            {
                try
                {
                    await this.ApplySourcePolicyAsync(run).ConfigureAwait(false);
                    if (run.ReadFailed || run.TerminalFault)
                    {
                        return;
                    }
                }
                catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    this.RecordFailure(run, "apply source policy", ex, terminal: false);
                    return;
                }

                continue;
            }

            if (!create)
            {
                if (!await this.StopRunAsync(run).ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }

            try
            {
                run.Backend = entry.Registration.CreateBackend(this._loggerFactory)
                    ?? throw new InvalidOperationException("The provider factory returned no backend.");
                run.Cancellation.Token.ThrowIfCancellationRequested();
                await this.ApplySourcePolicyAsync(run).ConfigureAwait(false);
                await run.Backend.StartAsync(run.Cancellation.Token).ConfigureAwait(false);
                run.Cancellation.Token.ThrowIfCancellationRequested();
                Task refresh;
                lock (this._stateLock)
                {
                    run.Started = true;
                    run.WatchTask = Task.Run(() => this.WatchProviderAsync(run));
                    refresh = run.AppliedPolicyRevision == entry.SourcePolicy.Revision
                        ? this.StartRefreshUnderLock(run)
                        : Task.CompletedTask;
                }

                await refresh.ConfigureAwait(false);
                if (run.Cancellation.IsCancellationRequested)
                {
                    continue;
                }

                lock (this._stateLock)
                {
                    ValidateSourcePolicySnapshotUnderLock(run, run.AppliedPolicyRevision);
                }

                if (run.ReadFailed || run.TerminalFault)
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                this.RecordFailure(run, "start", ex, terminal: true);
                if (!await this.StopRunAsync(run).ConfigureAwait(false))
                {
                    return;
                }

                lock (this._stateLock)
                {
                    this.SetLifecycleStateUnderLock(entry,
                        entry.Enabled ? MediaBackendLifecycleStatus.Faulted : MediaBackendLifecycleStatus.Disabled, entry.Error);
                }

                return;
            }
        }
    }

    private async Task ApplySourcePolicyAsync(ProviderRun run)
    {
        MediaBackendSourcePolicy policy;
        Task previousRead;
        lock (this._stateLock)
        {
            policy = run.Entry.SourcePolicy;
            previousRead = run.RefreshTask;
            run.ApplyingPolicy = true;
        }

        try
        {
            if (run.Backend is IMediaSourcePolicyBackend backend)
            {
                await backend.ApplySourcePolicyAsync(policy, run.Cancellation.Token).ConfigureAwait(false);
            }
            else if (!policy.ExcludedApplicationIds.IsEmpty)
            {
                throw new InvalidOperationException("The replaced backend does not support source policies.");
            }

            await previousRead.ConfigureAwait(false);
            Task refresh;
            lock (this._stateLock)
            {
                run.AppliedPolicyRevision = policy.Revision;
                refresh = run.Started && !run.Retired && policy == run.Entry.SourcePolicy
                    ? this.StartRefreshUnderLock(run)
                    : Task.CompletedTask;
            }

            await refresh.ConfigureAwait(false);
            lock (this._stateLock)
            {
                ValidateSourcePolicySnapshotUnderLock(run, policy.Revision);
            }
        }
        finally
        {
            lock (this._stateLock)
            {
                run.ApplyingPolicy = false;
                if (run.Dirty && !run.Retired)
                {
                    this.Signal(MediaBackendSignal.ObservationsChanged);
                }
            }
        }
    }

    private static void ValidateSourcePolicySnapshotUnderLock(ProviderRun run, long policyRevision)
    {
        if (run.Started && !run.Retired && !run.ReadFailed && !run.TerminalFault && policyRevision == run.Entry.SourcePolicy.Revision &&
            run.PublishedPolicyRevision != policyRevision)
        {
            throw new InvalidOperationException("The backend did not publish a snapshot for the applied source policy.");
        }
    }

    private async Task WatchProviderAsync(ProviderRun run)
    {
        try
        {
            await foreach (var signal in run.Backend!.WatchAsync(run.Cancellation.Token).ConfigureAwait(false))
            {
                lock (this._stateLock)
                {
                    if (run.Retired)
                    {
                        return;
                    }

                    run.Dirty = true;
                }

                this.Signal(signal);
            }

            run.Cancellation.Token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The provider notification stream ended.");
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            this.RecordFailure(run, "watch", ex, terminal: true);
        }
    }

    private Task StartRefreshUnderLock(ProviderRun run)
    {
        run.Reading = true;
        run.Dirty = false;
        var policyRevision = run.AppliedPolicyRevision;
        run.RefreshTask = Task.Run(() => this.RefreshProviderAsync(run, policyRevision));
        return run.RefreshTask;
    }

    private async Task RefreshProviderAsync(ProviderRun run, long policyRevision)
    {
        var changes = MediaBackendSignal.ObservationsChanged | MediaBackendSignal.BackendsChanged;
        try
        {
            var snapshot = await run.Backend!.ReadSnapshotAsync(run.Cancellation.Token).ConfigureAwait(false);
            lock (this._stateLock)
            {
                if (run.Retired || run.TerminalFault || snapshot.Revision < (run.Snapshot?.Revision ?? -1))
                {
                    return;
                }

                if (policyRevision != run.Entry.SourcePolicy.Revision ||
                    (run.Backend is IMediaSourcePolicyBackend && snapshot.SourcePolicyRevision != policyRevision))
                {
                    run.Dirty = true;
                    return;
                }

                ValidateConnectionState(snapshot.Connection);
                foreach (var session in snapshot.Sessions)
                {
                    ValidateSource(session.MediaProperties.Source);
                }

                var excluded = run.Entry.SourcePolicy.ExcludedApplicationIds;
                snapshot = snapshot with
                {
                    Sessions = [.. snapshot.Sessions.Where(session => !IsExcluded(session, excluded))],
                };

                var ids = snapshot.Sessions.Select(static session => session.Id).ToHashSet();
                if (ids.Count != snapshot.Sessions.Length)
                {
                    throw new InvalidOperationException("The provider returned duplicate session IDs.");
                }

                if (run.Snapshot is null || !snapshot.Sessions.Select(static session => session.Id)
                        .SequenceEqual(run.Snapshot.Sessions.Select(static session => session.Id)))
                {
                    changes |= MediaBackendSignal.SessionsChanged;
                }

                if (run.Snapshot is null || !run.Snapshot.CurrentSessionHints.SequenceEqual(snapshot.CurrentSessionHints) ||
                    run.Snapshot?.Availability != snapshot.Availability || run.ReadFailed)
                {
                    changes |= MediaBackendSignal.CurrentSessionChanged;
                }

                foreach (var removed in run.Routes.Keys.Where(id => !ids.Contains(id)).ToArray())
                {
                    this._routes.Remove(run.Routes[removed].Id);
                    run.Routes.Remove(removed);
                }

                foreach (var local in snapshot.Sessions)
                {
                    if (!run.Routes.TryGetValue(local.Id, out var route))
                    {
                        route = new SessionRoute(run, new(++this._nextSessionId), local);
                        run.Routes.Add(local.Id, route);
                        this._routes.Add(route.Id, route);
                    }

                    if (local.MediaProperties.Artwork is null)
                    {
                        route.Artwork = null;
                    }
                    else if (route.Artwork is null || local.MediaProperties.Artwork != route.Local.MediaProperties.Artwork ||
                             local.BindingGeneration != route.Local.BindingGeneration)
                    {
                        route.Artwork = new(new(route.Id.Value), ++this._nextArtworkVersion);
                    }

                    route.Local = local;
                }

                run.Snapshot = snapshot;
                run.PublishedPolicyRevision = policyRevision;
                run.ReadFailed = false;
                this.SetLifecycleStateUnderLock(run.Entry, MediaBackendLifecycleStatus.Ready, null);
                this._revision++;
            }

            this.Signal(changes);
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            this.RecordFailure(run, "read snapshot", ex, terminal: false);
        }
        finally
        {
            lock (this._stateLock)
            {
                run.Reading = false;
                if (run.Dirty && !run.Retired)
                {
                    this.Signal(MediaBackendSignal.ObservationsChanged);
                }
            }
        }
    }

    private static void ValidateConnectionState(MediaBackendConnectionState connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!Enum.IsDefined(connection.Status) || connection.Connections.IsDefault)
        {
            throw new InvalidOperationException("The provider returned an invalid connection state.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in connection.Connections)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.DisplayName) ||
                !Enum.IsDefined(item.Status) || !ids.Add(item.Id))
            {
                throw new InvalidOperationException("Provider connections must have valid states and distinct, nonempty identities.");
            }
        }
    }

    private void RecordFailure(ProviderRun run, string operation, Exception exception, bool terminal)
    {
        lock (this._stateLock)
        {
            if (run.Retired)
            {
                return;
            }

            run.TerminalFault |= terminal;
            run.ReadFailed = true;
            this.SetLifecycleStateUnderLock(run.Entry, MediaBackendLifecycleStatus.Faulted, exception.Message);
            this._revision++;
        }

        MediaBackendLog.ProviderFailed(this._logger, run.Entry.Registration.Id, operation, exception);
        this.Signal(MediaBackendSignal.ObservationsChanged | MediaBackendSignal.CurrentSessionChanged | MediaBackendSignal.BackendsChanged);
    }

    private void RetireUnderLock(ProviderRun run)
    {
        if (run.Retired)
        {
            return;
        }

        run.Retired = true;
        foreach (var route in run.Routes.Values)
        {
            this._routes.Remove(route.Id);
        }

        if (run.ActiveUses == 0)
        {
            run.Drained.TrySetResult();
        }

        this._revision++;
        this.Signal(MediaBackendSignal.SessionsChanged | MediaBackendSignal.CurrentSessionChanged | MediaBackendSignal.BackendsChanged);
    }

    private async Task<bool> StopRunAsync(ProviderRun run)
    {
        lock (this._stateLock)
        {
            this.RetireUnderLock(run);
            if (run.DisposalFailed)
            {
                this.SetLifecycleStateUnderLock(run.Entry, MediaBackendLifecycleStatus.Faulted, run.Entry.Error);
                return false;
            }
        }

        await this.CancelRunAsync(run).ConfigureAwait(false);
        await Task.WhenAll(run.WatchTask, run.RefreshTask, run.Drained.Task).ConfigureAwait(false);
        try
        {
            if (run.Backend is not null)
            {
                await run.Backend.DisposeAsync().ConfigureAwait(false);
            }

            run.Cancellation.Dispose();
            lock (this._stateLock)
            {
                run.Entry.Run = null;
                this._revision++;
                this.Signal(MediaBackendSignal.BackendsChanged);
            }

            return true;
        }
        catch (Exception ex)
        {
            lock (this._stateLock)
            {
                run.DisposalFailed = true;
                this.SetLifecycleStateUnderLock(run.Entry, MediaBackendLifecycleStatus.Faulted, ex.Message);
            }

            MediaBackendLog.ProviderFailed(this._logger, run.Entry.Registration.Id, "dispose", ex);
            return false;
        }
    }

    private async Task CancelRunAsync(ProviderRun run)
    {
        try
        {
            await run.Cancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            MediaBackendLog.ProviderFailed(this._logger, run.Entry.Registration.Id, "cancel", ex);
        }
    }

    private SessionUse? TryAcquire(MediaBackendSessionId id, long? generation, out MediaBackendCommandStatus failure)
    {
        lock (this._stateLock)
        {
            var route = this.FindAvailableRouteUnderLock(id, generation, out failure);
            if (route is null)
            {
                return null;
            }

            route.Run.ActiveUses++;
            return new(this, id, route.Run, route.Local);
        }
    }

    private SessionRoute? FindAvailableRouteUnderLock(MediaBackendSessionId id, long? generation, out MediaBackendCommandStatus failure)
    {
        failure = MediaBackendCommandStatus.SessionGone;
        if (!this._routes.TryGetValue(id, out var route) || route.Run.Retired ||
            (generation.HasValue && route.Local.BindingGeneration != generation.Value) || !route.Local.IsAvailable)
        {
            return null;
        }

        if (!IsAvailable(route))
        {
            failure = MediaBackendCommandStatus.Unavailable;
            return null;
        }

        return route;
    }

    private void Release(ProviderRun run)
    {
        lock (this._stateLock)
        {
            run.ActiveUses--;
            if (run.Retired && run.ActiveUses == 0)
            {
                run.Drained.TrySetResult();
            }
        }
    }

    private async Task<ImmutableArray<MediaBackendPauseResult>> PauseOthersAsync(
        MediaBackendCommand command, CancellationToken cancellationToken)
    {
        MediaBackendSessionTarget[][] groups;
        lock (this._stateLock)
        {
            // A provider may share one control lane across its sessions.
            groups = [.. command.SessionsToPause
                .Where(session => session.SessionId != command.SessionId)
                .Distinct()
                .GroupBy(session => this._routes.GetValueOrDefault(session.SessionId)?.Run)
                .Select(static group => group.ToArray())];
        }

        var results = await Task.WhenAll(groups.Select(group => this.PauseProviderSessionsAsync(group, cancellationToken))).ConfigureAwait(false);
        return [.. results.SelectMany(static group => group)];
    }

    private async Task<ImmutableArray<MediaBackendPauseResult>> PauseProviderSessionsAsync(
        MediaBackendSessionTarget[] targets, CancellationToken cancellationToken)
    {
        var results = ImmutableArray.CreateBuilder<MediaBackendPauseResult>(targets.Length);
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await this.PauseOtherAsync(target, cancellationToken).ConfigureAwait(false));
        }

        return results.MoveToImmutable();
    }

    private async Task<MediaBackendPauseResult> PauseOtherAsync(MediaBackendSessionTarget target, CancellationToken cancellationToken)
    {
        var use = this.TryAcquire(target.SessionId, target.BindingGeneration, out var failure);
        if (use is null)
        {
            return new(target, failure, "The secondary session is unavailable or has been replaced.");
        }

        if (!use.Local.Capabilities.HasFlag(MediaCapabilities.Pause))
        {
            use.Dispose();
            return new(target, MediaBackendCommandStatus.Unsupported, "The secondary session does not support pause.");
        }

        var result = await this.ExecuteWithUseAsync(use, MediaOperation.Pause, cancellationToken).ConfigureAwait(false);
        if (result.Status != MediaBackendCommandStatus.Completed)
        {
            MediaBackendLog.PauseFailed(this._logger, use.Run.Entry.Registration.Id, result.Status);
        }

        return new(target, result.Status, result.DiagnosticMessage);
    }

    private async Task<MediaBackendCommandResult> ExecuteWithUseAsync(SessionUse use, MediaOperation operation, CancellationToken cancellationToken)
    {
        var cancellation = this.CreateOperationCancellation(use, cancellationToken);
        var token = cancellation.Token;
        var pending = this.ExecuteCoreAsync(use, operation, cancellation);
        try
        {
            return await pending.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(MediaBackendCommandStatus.Unavailable, "The provider stopped or the command timed out.");
        }
    }

    private CancellationTokenSource CreateOperationCancellation(SessionUse use, CancellationToken cancellationToken)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, use.Run.Cancellation.Token);
        cancellation.CancelAfter(this._operationTimeout);
        return cancellation;
    }

    private async Task<MediaBackendCommandResult> ExecuteCoreAsync(SessionUse use, MediaOperation operation, CancellationTokenSource cancellation)
    {
        using (use)
        using (cancellation)
        {
            var binding = new MediaBackendSessionTarget(use.Local.Id, use.Local.BindingGeneration);
            var entered = false;
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                lock (this._stateLock)
                {
                    if (this.FindAvailableRouteUnderLock(use.Id, use.Local.BindingGeneration, out var failure) is null)
                    {
                        return new(failure, "The target session is unavailable or has been replaced.");
                    }

                    entered = use.Run.ActiveCommands.Add(binding);
                    if (!entered)
                    {
                        return new(MediaBackendCommandStatus.Unavailable, "A previous command is still running for this session binding.");
                    }
                }

                return await use.Run.Backend!.ExecuteAsync(new(use.Local.Id, use.Local.BindingGeneration, operation, []),
                    cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new(MediaBackendCommandStatus.Unavailable, "The provider command was canceled.");
            }
            catch (Exception ex)
            {
                MediaBackendLog.ProviderFailed(this._logger, use.Run.Entry.Registration.Id, "execute command", ex);
                return new(MediaBackendCommandStatus.Failed, ex.Message);
            }
            finally
            {
                lock (this._stateLock)
                {
                    if (entered)
                    {
                        use.Run.ActiveCommands.Remove(binding);
                    }

                    if (operation != MediaOperation.ActivateSource)
                    {
                        use.Run.Dirty = true;
                    }
                }

                if (operation != MediaOperation.ActivateSource)
                {
                    this.Signal(MediaBackendSignal.ObservationsChanged);
                }
            }
        }
    }

    private async Task<MediaArtworkContent?> ReadArtworkCoreAsync(SessionUse use, MediaArtworkKey key, CancellationTokenSource cancellation)
    {
        using (use)
        using (cancellation)
        {
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                return await use.Run.Backend!.GetArtworkAsync(key, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                MediaBackendLog.ProviderFailed(this._logger, use.Run.Entry.Registration.Id, "read artwork", ex);
                return null;
            }
        }
    }

    private async Task FinishDisposalAsync()
    {
        await Task.WhenAll(this._providers.Select(static entry => entry.Transition)).ConfigureAwait(false);
        this._signals.Writer.TryComplete();
    }

    private void Signal(MediaBackendSignal signal)
    {
        Interlocked.Or(ref this._pendingSignals, (int)signal);
        this._signals.Writer.TryWrite(true);
    }

    private static bool IsAvailable(SessionRoute route) => route.Local.IsAvailable && !route.Run.Retired &&
        !route.Run.ReadFailed && !route.Run.TerminalFault && route.Run.Snapshot?.Availability == MediaControlAvailability.Available;

    private static bool IsExcluded(MediaBackendSessionSnapshot session, ImmutableHashSet<string> excluded) =>
        session.MediaProperties.Source.NativeApplication is { } application && excluded.Contains(application.ApplicationId);

    private static void ValidateSource(MediaSourceSnapshot source)
    {
        if (source is null || source.Details.IsDefault ||
            source.NativeApplication is { } application && string.IsNullOrWhiteSpace(application.ApplicationId) ||
            source.Details.Any(static detail => detail is null || string.IsNullOrWhiteSpace(detail.Label) || detail.Value is null))
        {
            throw new InvalidOperationException("The provider returned invalid source presentation.");
        }
    }

    private static MediaBackendSessionSnapshot Present(SessionRoute route) => route.Local with
    {
        Id = route.Id,
        IsAvailable = IsAvailable(route),
        MediaProperties = route.Local.MediaProperties with
        {
            Artwork = route.Artwork,
            Source = route.Local.MediaProperties.Source with { Provider = route.Run.Entry.SourceProvider },
        },
    };

    private sealed class ProviderEntry(MediaBackendRegistration registration, bool enabled)
    {
        public MediaBackendRegistration Registration { get; } = registration;
        public MediaSourceProvider SourceProvider { get; } = new(registration.Id, registration.DisplayName);
        public bool Enabled { get; set; } = enabled;
        public MediaBackendLifecycleStatus Status { get; set; }
        public string? Error { get; set; }
        public Task Transition { get; set; } = Task.CompletedTask;
        public ProviderRun? Run { get; set; }
        public MediaBackendSourcePolicy SourcePolicy { get; set; } = MediaBackendSourcePolicy.Empty;
    }

    private sealed class ProviderRun(ProviderEntry entry)
    {
        public ProviderEntry Entry { get; } = entry;
        public IMediaBackend? Backend { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Dictionary<MediaBackendSessionId, SessionRoute> Routes { get; } = [];
        public HashSet<MediaBackendSessionTarget> ActiveCommands { get; } = [];
        public Task WatchTask { get; set; } = Task.CompletedTask;
        public Task RefreshTask { get; set; } = Task.CompletedTask;
        public MediaBackendSnapshot? Snapshot { get; set; }
        public int ActiveUses { get; set; }
        public bool Started { get; set; }
        public bool Retired { get; set; }
        public bool TerminalFault { get; set; }
        public bool ReadFailed { get; set; }
        public bool DisposalFailed { get; set; }
        public bool Reading { get; set; }
        public bool Dirty { get; set; }
        public bool ApplyingPolicy { get; set; }
        public long AppliedPolicyRevision { get; set; } = -1;
        public long PublishedPolicyRevision { get; set; } = -1;
    }

    private sealed class SessionRoute(ProviderRun run, MediaBackendSessionId id, MediaBackendSessionSnapshot local)
    {
        public ProviderRun Run { get; } = run;
        public MediaBackendSessionId Id { get; } = id;
        public MediaBackendSessionSnapshot Local { get; set; } = local;
        public MediaArtworkKey? Artwork { get; set; }
    }

    private sealed class SessionUse(CompositeMediaBackend owner, MediaBackendSessionId id, ProviderRun run, MediaBackendSessionSnapshot local) : IDisposable
    {
        private CompositeMediaBackend? _owner = owner;
        public MediaBackendSessionId Id { get; } = id;
        public ProviderRun Run { get; } = run;
        public MediaBackendSessionSnapshot Local { get; } = local;
        public void Dispose() => Interlocked.Exchange(ref this._owner, null)?.Release(this.Run);
    }
}