using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting;

/// <summary>Compiled worker launch configuration; paths and callbacks are supplied by the extension.</summary>
public sealed record WorkerOptions(string Executable, string BackendId)
{
    /// <summary>Launch through handshake, policy acknowledgement and the first valid snapshot.</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Separate command admission/completion and frame queue/write budgets; unsent timeouts preserve the connection.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Budget for each snapshot read, consecutive read-failure recovery, and artwork.</summary>
    public TimeSpan ObservationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Policy budget including any preceding snapshot read and backend binding cleanup.</summary>
    public TimeSpan PolicyTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Graceful process-exit wait; the owner also enforces an independent shutdown deadline.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Initial recovery delay, doubled after failures and capped at two seconds.</summary>
    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Replacement budget, reset after thirty continuous seconds of connected, available state under the current policy.</summary>
    public int MaximumRestarts { get; init; } = 3;

    /// <summary>Optional worker log settings, captured for this proxy's lifetime.</summary>
    public WorkerLoggingOptions? Logging { get; init; }

    /// <summary>Owner-side callback for one current activation command; must honor its cancellation token.</summary>
    public Func<HostedSourceActivation, CancellationToken, Task<bool>>? ActivateSource { get; init; }
}

/// <summary>Hosts a leaf backend in an owner-bound worker and fences session identities across recovery.</summary>
public sealed partial class OutOfProcessMediaBackend : IMediaSourcePolicyBackend
{
    private static readonly Action<ILogger, string, int, int, Guid, int, string, Exception?> WorkerEvent
        = LoggerMessage.Define<string, int, int, Guid, int, string>(
            LogLevel.Information, new EventId(1, "MediaWorker"),
            "Backend {BackendId}, owner {OwnerPid}, worker {WorkerPid}, epoch {Epoch}, restarts {RestartCount}: {Reason}");

    private static readonly Action<ILogger, string, string, Exception?> SnapshotReadFailed
        = LoggerMessage.Define<string, string>(
            LogLevel.Warning, new EventId(2, nameof(SnapshotReadFailed)),
            "Worker backend {BackendId} could not read a snapshot: {Reason}");

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<MediaBackendSessionId, MediaBackendSessionId> _localIds = [];
    private readonly ILogger _logger;
    private readonly WorkerOptions _options;
    private readonly MediaWorkerOwner _owner;
    private readonly SemaphoreSlim _policyRequests = new(1, 1);
    private readonly Dictionary<MediaBackendSessionId, MediaBackendSessionSnapshot> _routes = [];

    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest
    });

    private Connection? _connection;
    private Task? _disposal;
    private int _forcedTerminations;
    private long _nextSessionId;
    private string? _observationFailure;
    private MediaBackendSourcePolicy _policy = MediaBackendSourcePolicy.Empty;
    private int _restarts;
    private long _revision;
    private MediaBackendSnapshot _snapshot = new(0, [], [], MediaControlAvailability.Unavailable);
    private Task? _supervisor;
    private Exception? _unconfirmedExit;
    private Guid _workerEpoch;
    private string? _workerPackageFullName;
    private int _workerProcessId;
    private long _workerRevision = -1;

    /// <summary>Gets the current worker PID, or zero after its process handle has been released.</summary>
    public int WorkerProcessId
    {
        get
        {
            lock (this._gate) { return this._workerProcessId; }
        }
    }

    /// <summary>Gets the most recently welcomed connection epoch; this is diagnostic state, not command admission.</summary>
    public Guid WorkerEpoch
    {
        get
        {
            lock (this._gate) { return this._workerEpoch; }
        }
    }

    /// <summary>Gets the last launched worker's kernel package identity, or null for an unpackaged process.</summary>
    public string? WorkerPackageFullName
    {
        get
        {
            lock (this._gate) { return this._workerPackageFullName; }
        }
    }

    /// <summary>Gets total replacement launches during this proxy's lifetime.</summary>
    public int RestartCount => Volatile.Read(ref this._restarts);

    /// <summary>Gets failed graceful exits that required closing the worker job.</summary>
    public int ForcedTerminationCount => Volatile.Read(ref this._forcedTerminations);

    /// <summary>Creates a lazy proxy; the owner can stop it independently of media-service cleanup.</summary>
    public OutOfProcessMediaBackend(WorkerOptions options, MediaWorkerOwner owner, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BackendId);
        foreach (var duration in new[]
                 {
                     options.StartupTimeout, options.RequestTimeout, options.ObservationTimeout,
                     options.PolicyTimeout, options.ShutdownTimeout, options.RestartDelay
                 })
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(duration, TimeSpan.FromMilliseconds(uint.MaxValue - 1));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(options.MaximumRestarts);
        this._options = options;
        this._owner = owner;
        this._logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<OutOfProcessMediaBackend>();
        owner.Attach(this);
    }

    internal OutOfProcessMediaBackend(WorkerOptions options) : this(options, new MediaWorkerOwner())
    {
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this._gate)
        {
            ObjectDisposedException.ThrowIf(this._disposal is not null, this);
            if (this._supervisor is not null)
            {
                throw new InvalidOperationException("The proxy has already started.");
            }

            this._supervisor = Task.Run(this.SuperviseAsync, CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<MediaBackendSignal> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var _ in this._signals.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return MediaBackendSignal.ObservationsChanged;
        }
    }

    public Task<MediaBackendSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this._gate)
        {
            if (this._observationFailure is { } failure) { throw new IOException(failure); }

            return Task.FromResult(this._snapshot);
        }
    }

    public async Task ApplySourcePolicyAsync(MediaBackendSourcePolicy policy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this._gate)
        {
            ObjectDisposedException.ThrowIf(this._disposal is not null, this);
            if (policy.Revision < this._policy.Revision || (policy.Revision == this._policy.Revision &&
                                                            !policy.ExcludedApplicationIds.SetEquals(this._policy
                                                                .ExcludedApplicationIds)))
            {
                throw new ArgumentException("Source policy revisions must increase when contents change.",
                    nameof(policy));
            }

            if (policy.Revision != this._policy.Revision) { this._connection?.Health.Observe(false); }

            this._policy = policy;
            var sessions = this._snapshot.Sessions.Where(session =>
                session.MediaProperties.Source.NativeApplication is not { } native ||
                !policy.ExcludedApplicationIds.Contains(native.ApplicationId)).ToImmutableArray();
            var retained = sessions.Select(static session => session.Id).ToHashSet();
            foreach (var removed in this._routes.Keys.Where(id => !retained.Contains(id)).ToArray())
            {
                this._localIds.Remove(this._routes[removed].Id);
                this._routes.Remove(removed);
            }

            this._snapshot = this._snapshot with
            {
                Revision = ++this._revision,
                SourcePolicyRevision = policy.Revision,
                Sessions = sessions,
                CurrentSessionHints = [.. this._snapshot.CurrentSessionHints.Where(retained.Contains)]
            };
            this._signals.Writer.TryWrite(true);
        }

        await this.SendCurrentPolicyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MediaBackendCommandResult> ExecuteAsync(
        MediaBackendCommand command,
        CancellationToken cancellationToken)
    {
        Connection? connection;
        MediaBackendSessionSnapshot? route;
        lock (this._gate)
        {
            connection = this._connection;
            this._routes.TryGetValue(command.SessionId, out route);
            if (route is null || route.BindingGeneration != command.BindingGeneration || !route.IsAvailable)
            {
                return new MediaBackendCommandResult(MediaBackendCommandStatus.SessionGone,
                    "The captured worker binding is obsolete.");
            }

            if (connection is null || this._observationFailure is not null ||
                this._snapshot.Availability != MediaControlAvailability.Available)
            {
                return new MediaBackendCommandResult(MediaBackendCommandStatus.Unavailable,
                    "The worker is unavailable.");
            }
        }

        if (!command.SessionsToPause.IsEmpty)
        {
            throw new ArgumentException("The composite must coordinate secondary pauses before calling this leaf.",
                nameof(command));
        }

        try
        {
            var result = await connection.ExecuteAsync(command with { SessionId = route.Id }, cancellationToken)
                .ConfigureAwait(false);
            lock (this._gate)
            {
                if (connection != this._connection || !this._routes.TryGetValue(command.SessionId, out var current) ||
                    current.BindingGeneration != route.BindingGeneration || current.Id != route.Id)
                {
                    return new MediaBackendCommandResult(MediaBackendCommandStatus.SessionGone,
                        "The worker binding changed during execution.");
                }
            }

            return result ?? new MediaBackendCommandResult(MediaBackendCommandStatus.Failed,
                "The worker returned no command result.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                                   ex is IOException or TimeoutException or OperationCanceledException)
        {
            return new MediaBackendCommandResult(MediaBackendCommandStatus.Unavailable, ex.Message);
        }
    }

    public async ValueTask<MediaArtworkContent?> GetArtworkAsync(
        MediaArtworkKey key,
        CancellationToken cancellationToken)
    {
        Connection? connection;
        MediaArtworkKey? remote;
        lock (this._gate)
        {
            connection = this._connection;
            remote = this._routes.GetValueOrDefault(new MediaBackendSessionId(key.SessionId.Value))?.MediaProperties
                .Artwork;
            if (connection is null || this._observationFailure is not null || remote is null ||
                remote.Value.Version != key.Version)
            {
                return null;
            }
        }

        try
        {
            var artwork = await connection.GetArtworkAsync(remote.Value, cancellationToken).ConfigureAwait(false);
            lock (this._gate)
            {
                return connection == this._connection &&
                       this._routes.GetValueOrDefault(new MediaBackendSessionId(key.SessionId.Value))?.MediaProperties
                           .Artwork == remote
                    ? artwork
                    : null;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                                   ex is IOException or TimeoutException or OperationCanceledException)
        {
            return null;
        }
    }

    public void InvalidateObservations(ImmutableArray<MediaBackendObservationRequest> requests)
    {
        Connection? connection;
        ImmutableArray<MediaBackendObservationRequest> translated;
        lock (this._gate)
        {
            connection = this._connection;
            translated =
            [
                .. requests.Where(request => this._routes.ContainsKey(request.SessionId))
                    .Select(request => request with { SessionId = this._routes[request.SessionId].Id })
            ];
        }

        if (connection is not null && !translated.IsEmpty)
        {
            connection.Invalidate(translated);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (this._gate)
        {
            this.PublishUnavailableUnderLock(MediaBackendConnectionState.Disconnected);
            return new ValueTask(this._disposal ??= Task.Run(this.StopAsync));
        }
    }

    private async Task SendCurrentPolicyAsync(CancellationToken cancellationToken)
    {
        await this._policyRequests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Connection? connection;
            MediaBackendSourcePolicy policy;
            lock (this._gate)
            {
                connection = this._connection;
                policy = this._policy;
            }

            if (connection is null || connection.AppliedPolicyRevision == policy.Revision)
            {
                return;
            }

            try
            {
                var revision = await connection
                    .ApplyPolicyAsync(SourcePolicyMessage.FromPolicy(policy), cancellationToken).ConfigureAwait(false);
                if (revision != policy.Revision)
                {
                    throw new InvalidDataException("The worker did not acknowledge the source policy.");
                }

                connection.AppliedPolicyRevision = policy.Revision;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && connection.IsClosed &&
                                       ex is IOException or TimeoutException or OperationCanceledException
                                           or ObjectDisposedException)
            {
                // Local routes are fenced; the next worker receives the latest policy.
            }
        }
        finally
        {
            this._policyRequests.Release();
        }
    }

    internal void DisconnectWorker()
    {
        Connection? connection;
        lock (this._gate) { connection = this._connection; }

        connection?.Close();
    }

    private async Task StopAsync()
    {
        try
        {
            await this._lifetime.CancelAsync().ConfigureAwait(false);
            if (this._supervisor is not null)
            {
                await this._supervisor.ConfigureAwait(false);
            }

            if (this._unconfirmedExit is { } failure)
            {
                throw new IOException("Worker exit could not be confirmed.", failure);
            }
        }
        finally
        {
            lock (this._gate)
            {
                this.PublishUnavailableUnderLock(MediaBackendConnectionState.Disconnected);
                this._signals.Writer.TryComplete();
            }

            this._lifetime.Dispose();
            this._owner.Detach(this);
        }
    }

    private async Task SuperviseAsync()
    {
        try
        {
            await this.RunWorkerLoopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (this._gate)
            {
                this.PublishUnavailableUnderLock(new MediaBackendConnectionState(MediaConnectionStatus.Disconnected,
                    $"Worker supervision stopped: {ex.Message}"));
                WorkerEvent(this._logger, this._options.BackendId, Environment.ProcessId, this._workerProcessId,
                    this._workerEpoch, this.RestartCount, "Supervision stopped", ex);
            }

            this._signals.Writer.TryComplete(ex);
        }
    }

    private async Task RunWorkerLoopAsync()
    {
        var failures = 0;
        while (!this._lifetime.IsCancellationRequested)
        {
            var launchedAt = Stopwatch.GetTimestamp();
            OwnedWorkerProcess? process = null;
            Connection? connection = null;
            string? failure = null;
            var owner = Guid.NewGuid();
            var pipeName = $"LOCAL\\JPSoftworks.MediaBackendHost.{owner:N}";
            NamedPipeServerStream? pipe = null;
            WorkerRpcEndpoint? protocol = null;
            try
            {
                this._lifetime.Token.ThrowIfCancellationRequested();
                pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                MediaBackendSourcePolicy policy;
                lock (this._gate)
                {
                    this.PublishUnavailableUnderLock(MediaBackendConnectionState.Connecting);
                    this._localIds.Clear();
                    this._workerRevision = -1;
                    policy = this._policy;
                }

                using var startup = CancellationTokenSource.CreateLinkedTokenSource(this._lifetime.Token);
                startup.CancelAfter(this._options.StartupTimeout);
                process = this._owner.StartWorker(this._options.Executable,
                [
                    "--worker", pipeName, owner.ToString("D"),
                    Environment.ProcessId.ToString(CultureInfo.InvariantCulture), this._options.BackendId
                ]);
                lock (this._gate)
                {
                    this._workerProcessId = process.ProcessId;
                    this._workerPackageFullName = process.PackageFullName;
                }

                await pipe.WaitForConnectionAsync(startup.Token).ConfigureAwait(false);
                PipeProtocol.VerifyPeer(pipe, process.ProcessId, true);
                protocol = await WorkerRpcEndpoint.CreateAsync(pipe, true, startup.Token).ConfigureAwait(false);
                protocol.Handler.WriteTimeout = this._options.RequestTimeout;
                connection = new Connection(protocol, owner, this._options, startup.Token);
                connection.Listen(this);
                var welcome = await connection.Worker.InitializeAsync(new WorkerHello(PipeProtocol.Version, owner,
                            this._options.BackendId,
                            SourcePolicyMessage.FromPolicy(policy), this._options.ActivateSource is not null,
                            this._options.Logging,
                            this._options.RequestTimeout, this._options.ObservationTimeout,
                            this._options.PolicyTimeout),
                        startup.Token)
                    .WaitAsync(startup.Token).ConfigureAwait(false);
                if (welcome.Version != PipeProtocol.Version || welcome.Owner != owner || welcome.Epoch == Guid.Empty ||
                    welcome.CanActivateSource != this._options.ActivateSource is not null)
                {
                    throw new InvalidDataException("Invalid worker welcome.");
                }

                connection.Epoch = welcome.Epoch;
                connection.AppliedPolicyRevision = policy.Revision;
                lock (this._gate)
                {
                    this._connection = connection;
                    this._workerEpoch = connection.Epoch;
                }

                var read = connection.Reader!;
                await protocol.Rpc.NotifyAsync(nameof(IWorkerNotifications.Begin), connection.Epoch)
                    .WaitAsync(startup.Token).ConfigureAwait(false);
                await this.SendCurrentPolicyAsync(startup.Token).ConfigureAwait(false);
                await connection.FirstSnapshot.Task.WaitAsync(startup.Token).ConfigureAwait(false);
                WorkerEvent(this._logger, this._options.BackendId, Environment.ProcessId, process.ProcessId,
                    connection.Epoch, this.RestartCount,
                    $"Connected after {Stopwatch.GetElapsedTime(launchedAt).TotalMilliseconds:F0} ms", null);
                await read.WaitAsync(this._lifetime.Token).ConfigureAwait(false);
                failure = connection.Failure;
            }
            catch (Exception ex)
            {
                failure = connection?.Failure ?? ex.Message;
            }
            finally
            {
                var stoppedAt = Stopwatch.GetTimestamp();
                lock (this._gate)
                {
                    this._connection = null;
                    this.PublishUnavailableUnderLock(new MediaBackendConnectionState(MediaConnectionStatus.Disconnected,
                        failure));
                }

                if (connection is not null)
                {
                    try
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                        await protocol!.Rpc.NotifyAsync(nameof(IWorkerNotifications.Shutdown), connection.Epoch)
                            .WaitAsync(deadline.Token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Pipe closure and job termination cover failed shutdown delivery.
                    }

                    connection.Close();
                }

                protocol?.Dispose();
                pipe?.Dispose();
                if (process is not null)
                {
                    try
                    {
                        await this.StopWorkerProcessAsync(process, connection?.Epoch ?? Guid.Empty)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        try
                        {
                            var exit = process.HasExited ? $"0x{process.ExitCode:X8}" : "unconfirmed";
                            WorkerEvent(this._logger, this._options.BackendId, Environment.ProcessId, process.ProcessId,
                                connection?.Epoch ?? Guid.Empty,
                                this.RestartCount,
                                $"Stopped after {Stopwatch.GetElapsedTime(stoppedAt).TotalMilliseconds:F0} ms; exit {exit}; {failure}",
                                null);
                        }
                        catch (Exception ex)
                        {
                            WorkerEvent(this._logger, this._options.BackendId, Environment.ProcessId, process.ProcessId,
                                connection?.Epoch ?? Guid.Empty,
                                this.RestartCount, "Exit diagnostics unavailable", ex);
                        }
                        finally
                        {
                            this._owner.ReleaseWorker(process);
                            lock (this._gate) { this._workerProcessId = 0; }
                        }
                    }
                }

                if (connection?.Reader is { } reader)
                {
                    try { await reader.ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        WorkerEvent(this._logger, this._options.BackendId, Environment.ProcessId, 0, connection.Epoch,
                            this.RestartCount, "Reader cleanup failed", ex);
                    }
                    finally { await connection.DisposeAsync().ConfigureAwait(false); }
                }
            }

            if (this._unconfirmedExit is { } exitFailure)
            {
                throw new IOException("Worker exit could not be confirmed.", exitFailure);
            }

            if (this._lifetime.IsCancellationRequested)
            {
                return;
            }

            if (connection?.Health.WasStable == true)
            {
                failures = 0;
            }

            if (++failures > this._options.MaximumRestarts)
            {
                throw new IOException($"Worker restart budget exhausted: {failure}");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(Math.Min(2000,
                        this._options.RestartDelay.TotalMilliseconds * Math.Pow(2, failures - 1))),
                    this._lifetime.Token).ConfigureAwait(false);
                Interlocked.Increment(ref this._restarts);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task StopWorkerProcessAsync(OwnedWorkerProcess process, Guid epoch)
    {
        try
        {
            using var deadline = new CancellationTokenSource(this._options.ShutdownTimeout);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref this._forcedTerminations);
            WorkerEvent(this._logger, this._options.BackendId, Environment.ProcessId, process.ProcessId, epoch,
                this.RestartCount, "Closing worker job after graceful exit failed", ex);
        }

        try
        {
            process.CloseJob();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Keep the exclusive group blocked when process termination is unconfirmed.
            this._unconfirmedExit = ex;
            WorkerEvent(this._logger, this._options.BackendId, Environment.ProcessId, process.ProcessId, epoch,
                this.RestartCount, "Worker exit remains unconfirmed", ex);
        }
    }

    private Task<bool> ActivateAsync(
        Connection connection,
        HostedSourceActivation activation,
        CancellationToken cancellationToken)
    {
        lock (this._gate)
        {
            if (this._disposal is not null || connection != this._connection ||
                cancellationToken.IsCancellationRequested ||
                this._options.ActivateSource is null || this._observationFailure is not null ||
                this._snapshot.Availability != MediaControlAvailability.Available ||
                !this._routes.Values.Any(route => route.Id == activation.Target.SessionId && route.IsAvailable &&
                                                  route.BindingGeneration == activation.Target.BindingGeneration &&
                                                  route.MediaProperties.Source.NativeApplication?.ApplicationId ==
                                                  activation.ApplicationId) ||
                this._policy.ExcludedApplicationIds.Contains(activation.ApplicationId))
            {
                return Task.FromResult(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return this._options.ActivateSource(activation, cancellationToken);
    }

    private bool AcceptSnapshot(Connection connection, MediaBackendSnapshot snapshot)
    {
        lock (this._gate)
        {
            if (this._disposal is not null || connection != this._connection ||
                snapshot.SourcePolicyRevision != this._policy.Revision)
            {
                return false;
            }

            if (snapshot.Revision < this._workerRevision || snapshot.Sessions.IsDefault ||
                snapshot.CurrentSessionHints.IsDefault)
            {
                throw new InvalidDataException("Invalid worker snapshot revision or collection.");
            }

            this._workerRevision = snapshot.Revision;
            var sessions = ImmutableArray.CreateBuilder<MediaBackendSessionSnapshot>();
            this._routes.Clear();
            var present = new HashSet<MediaBackendSessionId>();
            foreach (var session in snapshot.Sessions)
            {
                if (session is null || session.MediaProperties?.Source is null || !present.Add(session.Id) ||
                    session.MediaProperties.Artwork is { } sourceArtwork &&
                    sourceArtwork.SessionId.Value != session.Id.Value)
                {
                    throw new InvalidDataException("Duplicate worker session ID.");
                }

                if (session.MediaProperties.Source.NativeApplication is { } native &&
                    this._policy.ExcludedApplicationIds.Contains(native.ApplicationId))
                {
                    throw new InvalidDataException("The worker published an excluded source.");
                }

                if (!this._localIds.TryGetValue(session.Id, out var local))
                {
                    local = new MediaBackendSessionId(++this._nextSessionId);
                    this._localIds.Add(session.Id, local);
                }

                this._routes.Add(local, session);
                var artwork = session.MediaProperties.Artwork is { } key
                    ? key with { SessionId = new MediaSessionId(local.Value) }
                    : (MediaArtworkKey?)null;
                sessions.Add(session with
                {
                    Id = local, MediaProperties = session.MediaProperties with { Artwork = artwork }
                });
            }

            if (snapshot.CurrentSessionHints.Any(id => !present.Contains(id)))
            {
                throw new InvalidDataException("The worker supplied an unknown current-session hint.");
            }

            foreach (var obsolete in this._localIds.Keys.Where(id => !present.Contains(id)).ToArray())
            {
                this._localIds.Remove(obsolete);
            }

            this._snapshot = snapshot with
            {
                Revision = ++this._revision,
                Sessions = sessions.ToImmutable(),
                CurrentSessionHints =
                [.. snapshot.CurrentSessionHints.Where(present.Contains).Select(id => this._localIds[id])]
            };
            this._observationFailure = null;
            connection.Health.Observe(snapshot.Connection.Status == MediaConnectionStatus.Connected &&
                                      snapshot.Availability == MediaControlAvailability.Available);
            this._signals.Writer.TryWrite(true);
            return true;
        }
    }

    private void AcceptSnapshotFailure(Connection connection, WorkerSnapshotFailure failure)
    {
        if (failure.Error is null || failure.SourcePolicyRevision < 0)
        {
            throw new InvalidDataException("Invalid snapshot failure.");
        }

        lock (this._gate)
        {
            if (this._disposal is not null || connection != this._connection ||
                failure.SourcePolicyRevision != this._policy.Revision ||
                this._observationFailure == failure.Error) { return; }

            this._observationFailure = failure.Error;
            connection.Health.Observe(false);
            this._signals.Writer.TryWrite(true);
        }

        SnapshotReadFailed(this._logger, this._options.BackendId, failure.Error, null);
    }

    private void PublishUnavailableUnderLock(MediaBackendConnectionState connection)
    {
        this._observationFailure = null;
        this._routes.Clear();
        this._snapshot = new MediaBackendSnapshot(++this._revision, [], [], MediaControlAvailability.Unavailable)
        {
            SourcePolicyRevision = this._policy.Revision, Connection = connection
        };
        this._signals.Writer.TryWrite(true);
    }

    private static async Task<bool> SendInvalidationsAsync(
        Connection connection,
        ImmutableArray<MediaBackendObservationRequest> requests)
    {
        try
        {
            await connection.InvalidateAsync(requests).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException) when (!connection.IsClosed)
        {
            return false;
        }
        catch (Exception ex)
        {
            connection.Close($"Observation invalidation failed: {ex.Message}");
            return true;
        }
    }
}