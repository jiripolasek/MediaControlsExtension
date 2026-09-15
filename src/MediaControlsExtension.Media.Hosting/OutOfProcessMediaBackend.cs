using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
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
public sealed class OutOfProcessMediaBackend : IMediaSourcePolicyBackend
{
    private static readonly Action<ILogger, string, int, int, Guid, int, string, Exception?> WorkerEvent = LoggerMessage.Define<string, int, int, Guid, int, string>(
        LogLevel.Information, new EventId(1, "MediaWorker"), "Backend {BackendId}, owner {OwnerPid}, worker {WorkerPid}, epoch {Epoch}, restarts {RestartCount}: {Reason}");
    private static readonly Action<ILogger, string, string, Exception?> SnapshotReadFailed = LoggerMessage.Define<string, string>(
        LogLevel.Warning, new EventId(2, nameof(SnapshotReadFailed)), "Worker backend {BackendId} could not read a snapshot: {Reason}");
    private readonly WorkerOptions _options;
    private readonly MediaWorkerOwner _owner;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _policyRequests = new(1, 1);
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
    });
    private readonly Dictionary<MediaBackendSessionId, MediaBackendSessionId> _localIds = [];
    private readonly Dictionary<MediaBackendSessionId, MediaBackendSessionSnapshot> _routes = [];
    private MediaBackendSourcePolicy _policy = MediaBackendSourcePolicy.Empty;
    private MediaBackendSnapshot _snapshot = new(0, [], [], MediaControlAvailability.Unavailable);
    private Connection? _connection;
    private Task? _supervisor;
    private Task? _disposal;
    private long _nextSessionId;
    private long _revision;
    private long _workerRevision = -1;
    private int _workerProcessId;
    private Guid _workerEpoch;
    private string? _workerPackageFullName;
    private int _restarts;
    private int _forcedTerminations;
    private Exception? _unconfirmedExit;
    private string? _observationFailure;

    /// <summary>Creates a lazy proxy; the owner can stop it independently of media-service cleanup.</summary>
    public OutOfProcessMediaBackend(WorkerOptions options, MediaWorkerOwner owner, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BackendId);
        foreach (var duration in new[] { options.StartupTimeout, options.RequestTimeout, options.ObservationTimeout,
            options.PolicyTimeout, options.ShutdownTimeout, options.RestartDelay })
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

    /// <summary>Gets the current worker PID, or zero after its process handle has been released.</summary>
    public int WorkerProcessId { get { lock (this._gate) { return this._workerProcessId; } } }
    /// <summary>Gets the most recently welcomed connection epoch; this is diagnostic state, not command admission.</summary>
    public Guid WorkerEpoch { get { lock (this._gate) { return this._workerEpoch; } } }
    /// <summary>Gets the last launched worker's kernel package identity, or null for an unpackaged process.</summary>
    public string? WorkerPackageFullName { get { lock (this._gate) { return this._workerPackageFullName; } } }
    /// <summary>Gets total replacement launches during this proxy's lifetime.</summary>
    public int RestartCount => Volatile.Read(ref this._restarts);
    /// <summary>Gets failed graceful exits that required closing the worker job.</summary>
    public int ForcedTerminationCount => Volatile.Read(ref this._forcedTerminations);

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

    public async IAsyncEnumerable<MediaBackendSignal> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
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
                !policy.ExcludedApplicationIds.SetEquals(this._policy.ExcludedApplicationIds)))
            {
                throw new ArgumentException("Source policy revisions must increase when contents change.", nameof(policy));
            }

            if (policy.Revision != this._policy.Revision) { this._connection?.Health.Observe(false); }
            this._policy = policy;
            var sessions = this._snapshot.Sessions.Where(session => session.MediaProperties.Source.NativeApplication is not { } native ||
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
                CurrentSessionHints = [.. this._snapshot.CurrentSessionHints.Where(retained.Contains)],
            };
            this._signals.Writer.TryWrite(true);
        }

        await this.SendCurrentPolicyAsync(cancellationToken).ConfigureAwait(false);
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
                var reply = await connection.RequestAsync(new(MessageKind.Policy, connection.Owner, connection.Epoch)
                {
                    Policy = SourcePolicyMessage.FromPolicy(policy),
                }, cancellationToken).ConfigureAwait(false);
                if (reply.Policy?.Revision != policy.Revision)
                {
                    throw new InvalidDataException("The worker did not acknowledge the source policy.");
                }
                connection.AppliedPolicyRevision = policy.Revision;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && connection.IsClosed &&
                ex is IOException or TimeoutException or OperationCanceledException or ObjectDisposedException)
            {
                // Local routes are fenced; the next worker receives the latest policy.
            }
        }
        finally
        {
            this._policyRequests.Release();
        }
    }

    public async Task<MediaBackendCommandResult> ExecuteAsync(MediaBackendCommand command, CancellationToken cancellationToken)
    {
        Connection? connection;
        MediaBackendSessionSnapshot? route;
        lock (this._gate)
        {
            connection = this._connection;
            this._routes.TryGetValue(command.SessionId, out route);
            if (route is null || route.BindingGeneration != command.BindingGeneration || !route.IsAvailable)
            {
                return new(MediaBackendCommandStatus.SessionGone, "The captured worker binding is obsolete.");
            }

            if (connection is null || this._observationFailure is not null || this._snapshot.Availability != MediaControlAvailability.Available)
            {
                return new(MediaBackendCommandStatus.Unavailable, "The worker is unavailable.");
            }
        }

        if (!command.SessionsToPause.IsEmpty)
        {
            throw new ArgumentException("The composite must coordinate secondary pauses before calling this leaf.", nameof(command));
        }

        try
        {
            var reply = await connection.RequestAsync(new(MessageKind.Execute, connection.Owner, connection.Epoch)
            {
                Command = command with { SessionId = route.Id },
            }, cancellationToken).ConfigureAwait(false);
            lock (this._gate)
            {
                if (connection != this._connection || !this._routes.TryGetValue(command.SessionId, out var current) ||
                    current.BindingGeneration != route.BindingGeneration || current.Id != route.Id)
                {
                    return new(MediaBackendCommandStatus.SessionGone, "The worker binding changed during execution.");
                }
            }

            return reply.Result ?? new(MediaBackendCommandStatus.Failed, "The worker returned no command result.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is IOException or TimeoutException or OperationCanceledException)
        {
            return new(MediaBackendCommandStatus.Unavailable, ex.Message);
        }
    }

    public async ValueTask<MediaArtworkContent?> GetArtworkAsync(MediaArtworkKey key, CancellationToken cancellationToken)
    {
        Connection? connection;
        MediaArtworkKey? remote;
        lock (this._gate)
        {
            connection = this._connection;
            remote = this._routes.GetValueOrDefault(new(key.SessionId.Value))?.MediaProperties.Artwork;
            if (connection is null || this._observationFailure is not null || remote is null || remote.Value.Version != key.Version)
            {
                return null;
            }
        }

        try
        {
            var reply = await connection.RequestAsync(new(MessageKind.Artwork, connection.Owner, connection.Epoch)
            {
                ArtworkKey = remote,
            }, cancellationToken).ConfigureAwait(false);
            lock (this._gate)
            {
                return connection == this._connection && this._routes.GetValueOrDefault(new(key.SessionId.Value))?.MediaProperties.Artwork == remote
                    ? reply.Artwork : null;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is IOException or TimeoutException or OperationCanceledException)
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
            translated = [.. requests.Where(request => this._routes.ContainsKey(request.SessionId))
                .Select(request => request with { SessionId = this._routes[request.SessionId].Id })];
        }

        if (connection is not null && !translated.IsEmpty)
        {
            connection.Invalidate(translated);
        }
    }

    internal void DisconnectWorker()
    {
        Connection? connection;
        lock (this._gate) { connection = this._connection; }
        connection?.Close();
    }

    public ValueTask DisposeAsync()
    {
        lock (this._gate)
        {
            this.PublishUnavailableUnderLock(MediaBackendConnectionState.Disconnected);
            return new(this._disposal ??= Task.Run(this.StopAsync));
        }
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
            if (this._unconfirmedExit is { } failure) { throw new IOException("Worker exit could not be confirmed.", failure); }
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
                this.PublishUnavailableUnderLock(new(MediaConnectionStatus.Disconnected, $"Worker supervision stopped: {ex.Message}"));
                WorkerEvent(this._logger, this._options.BackendId, Environment.ProcessId, this._workerProcessId, this._workerEpoch, this.RestartCount, "Supervision stopped", ex);
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
            PipeProtocol? protocol = null;
            try
            {
                this._lifetime.Token.ThrowIfCancellationRequested();
                pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                protocol = new PipeProtocol(pipe) { WriteTimeout = this._options.RequestTimeout };
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
                    ["--worker", pipeName, owner.ToString("D"), Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), this._options.BackendId]);
                lock (this._gate)
                {
                    this._workerProcessId = process.ProcessId;
                    this._workerPackageFullName = process.PackageFullName;
                }
                await pipe.WaitForConnectionAsync(startup.Token).ConfigureAwait(false);
                PipeProtocol.VerifyPeer(pipe, process.ProcessId, server: true);
                await protocol.WriteAsync(new(MessageKind.Hello, owner)
                {
                    BackendId = this._options.BackendId,
                    Policy = SourcePolicyMessage.FromPolicy(policy),
                    CanActivateSource = this._options.ActivateSource is not null,
                    Logging = this._options.Logging,
                    RequestTimeout = this._options.RequestTimeout,
                    ObservationTimeout = this._options.ObservationTimeout,
                    PolicyTimeout = this._options.PolicyTimeout,
                }, startup.Token).ConfigureAwait(false);
                var welcome = await protocol.ReadAsync(startup.Token).ConfigureAwait(false);
                if (welcome.Kind != MessageKind.Welcome || welcome.Version != PipeProtocol.Version || welcome.Owner != owner || welcome.Epoch == Guid.Empty ||
                    welcome.CanActivateSource != (this._options.ActivateSource is not null))
                {
                    throw new InvalidDataException("Invalid worker welcome.");
                }

                connection = new(protocol, owner, welcome.Epoch, this._options, startup.Token)
                {
                    AppliedPolicyRevision = policy.Revision,
                };
                lock (this._gate)
                {
                    this._connection = connection;
                    this._workerEpoch = connection.Epoch;
                }

                var currentConnection = connection;
                var read = Task.Run(() => connection.ReadLoopAsync(snapshot => this.AcceptSnapshot(currentConnection, snapshot),
                    failure => this.AcceptSnapshotFailure(currentConnection, failure),
                    (activation, token) => this.ActivateAsync(currentConnection, activation, token)));
                connection.Reader = read;
                await this.SendCurrentPolicyAsync(startup.Token).ConfigureAwait(false);
                await connection.FirstSnapshot.Task.WaitAsync(startup.Token).ConfigureAwait(false);
                WorkerEvent(this._logger, this._options.BackendId, Environment.ProcessId, process.ProcessId, connection.Epoch, this.RestartCount,
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
                    this.PublishUnavailableUnderLock(new(MediaConnectionStatus.Disconnected, failure));
                }

                if (connection is not null)
                {
                    try
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                        await protocol!.WriteAsync(new(MessageKind.Shutdown, owner, connection.Epoch), deadline.Token).ConfigureAwait(false);
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
                        await this.StopWorkerProcessAsync(process, connection?.Epoch ?? Guid.Empty).ConfigureAwait(false);
                    }
                    finally
                    {
                        try
                        {
                            var exit = process.HasExited ? $"0x{process.ExitCode:X8}" : "unconfirmed";
                            WorkerEvent(this._logger, this._options.BackendId, Environment.ProcessId, process.ProcessId, connection?.Epoch ?? Guid.Empty,
                                this.RestartCount, $"Stopped after {Stopwatch.GetElapsedTime(stoppedAt).TotalMilliseconds:F0} ms; exit {exit}; {failure}", null);
                        }
                        catch (Exception ex)
                        {
                            WorkerEvent(this._logger, this._options.BackendId, Environment.ProcessId, process.ProcessId, connection?.Epoch ?? Guid.Empty,
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

            if (this._unconfirmedExit is { } exitFailure) { throw new IOException("Worker exit could not be confirmed.", exitFailure); }

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
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(2000, this._options.RestartDelay.TotalMilliseconds * Math.Pow(2, failures - 1))),
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

    private Task<bool> ActivateAsync(Connection connection, HostedSourceActivation activation, CancellationToken cancellationToken)
    {
        lock (this._gate)
        {
            if (this._disposal is not null || connection != this._connection || cancellationToken.IsCancellationRequested ||
                this._options.ActivateSource is null || this._observationFailure is not null || this._snapshot.Availability != MediaControlAvailability.Available ||
                !this._routes.Values.Any(route => route.Id == activation.Target.SessionId && route.IsAvailable &&
                    route.BindingGeneration == activation.Target.BindingGeneration &&
                    route.MediaProperties.Source.NativeApplication?.ApplicationId == activation.ApplicationId) ||
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
            if (this._disposal is not null || connection != this._connection || snapshot.SourcePolicyRevision != this._policy.Revision)
            {
                return false;
            }

            if (snapshot.Revision < this._workerRevision || snapshot.Sessions.IsDefault || snapshot.CurrentSessionHints.IsDefault)
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
                    session.MediaProperties.Artwork is { } sourceArtwork && sourceArtwork.SessionId.Value != session.Id.Value)
                {
                    throw new InvalidDataException("Duplicate worker session ID.");
                }

                if (session.MediaProperties.Source.NativeApplication is { } native && this._policy.ExcludedApplicationIds.Contains(native.ApplicationId))
                {
                    throw new InvalidDataException("The worker published an excluded source.");
                }

                if (!this._localIds.TryGetValue(session.Id, out var local))
                {
                    local = new(++this._nextSessionId);
                    this._localIds.Add(session.Id, local);
                }

                this._routes.Add(local, session);
                var artwork = session.MediaProperties.Artwork is { } key ? key with { SessionId = new(local.Value) } : (MediaArtworkKey?)null;
                sessions.Add(session with { Id = local, MediaProperties = session.MediaProperties with { Artwork = artwork } });
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
                CurrentSessionHints = [.. snapshot.CurrentSessionHints.Where(present.Contains).Select(id => this._localIds[id])],
            };
            this._observationFailure = null;
            connection.Health.Observe(snapshot.Connection.Status == MediaConnectionStatus.Connected && snapshot.Availability == MediaControlAvailability.Available);
            this._signals.Writer.TryWrite(true);
            return true;
        }
    }

    private void AcceptSnapshotFailure(Connection connection, WireMessage failure)
    {
        if (failure.Error is null || failure.SourcePolicyRevision < 0) { throw new InvalidDataException("Invalid snapshot failure."); }
        lock (this._gate)
        {
            if (this._disposal is not null || connection != this._connection || failure.SourcePolicyRevision != this._policy.Revision ||
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
        this._snapshot = new(++this._revision, [], [], MediaControlAvailability.Unavailable)
        {
            SourcePolicyRevision = this._policy.Revision,
            Connection = connection,
        };
        this._signals.Writer.TryWrite(true);
    }

    private static async Task<bool> SendInvalidationsAsync(Connection connection, ImmutableArray<MediaBackendObservationRequest> requests)
    {
        try
        {
            await connection.RequestAsync(new(MessageKind.Invalidate, connection.Owner, connection.Epoch)
            {
                Invalidations = requests,
            }, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException) when (!connection.IsClosed)
        {
            return false;
        }
        catch (Exception)
        {
            connection.Protocol.Dispose();
            return true;
        }
    }

    private sealed class Connection(PipeProtocol protocol, Guid owner, Guid epoch, WorkerOptions options, CancellationToken startup) : IAsyncDisposable
    {
        private readonly Lock _requestGate = new();
        private readonly ConcurrentDictionary<long, PendingRequest> _pending = new();
        private readonly Dictionary<MediaBackendSessionId, MediaBackendObservationChanges> _invalidations = [];
        private bool _invalidating;
        private readonly SemaphoreSlim _admission = new(32, 32);
        private readonly SemaphoreSlim _artworkAdmission = new(PipeProtocol.MaximumArtworkRequests, PipeProtocol.MaximumArtworkRequests);
        private readonly CancellationTokenSource _closed = new();
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _nextRequestId;
        private int _active;
        private bool _closing;

        public PipeProtocol Protocol { get; } = protocol;
        public Guid Owner { get; } = owner;
        public Guid Epoch { get; } = epoch;
        public Task? Reader { get; set; }
        public string? Failure { get; private set; }
        public bool IsClosed { get { lock (this._requestGate) { return this._closing; } } }
        public TaskCompletionSource FirstSnapshot { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WorkerHealth Health { get; } = new();
        public long AppliedPolicyRevision { get; set; }

        public async Task<WireMessage> RequestAsync(WireMessage message, CancellationToken cancellationToken)
        {
            var pending = this.SendRequestAsync(message, cancellationToken);
            try { return await pending.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _ = ObserveCanceledRequestAsync(pending);
                throw;
            }
        }

        private static async Task ObserveCanceledRequestAsync(Task pending) =>
            await pending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        private async Task<WireMessage> SendRequestAsync(WireMessage message, CancellationToken cancellationToken)
        {
            CancellationTokenSource timeout;
            var duringStartup = message.Kind == MessageKind.Policy && !this.FirstSnapshot.Task.IsCompletedSuccessfully;
            lock (this._requestGate)
            {
                if (this._closing || this._active >= 64)
                {
                    throw new IOException("The worker connection has closed.");
                }

                timeout = CancellationTokenSource.CreateLinkedTokenSource(this._closed.Token,
                    duringStartup ? startup : CancellationToken.None);
                this._active++;
            }

            using var requestTimeout = timeout;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            using var admissionDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var budget = WorkerTimeouts.ForRequest(message.Kind, options.RequestTimeout, options.ObservationTimeout, options.PolicyTimeout);
            if (!duringStartup)
            {
                admissionDeadline.CancelAfter(budget);
            }
            long id = 0;
            var admitted = false;
            var artworkAdmitted = false;
            var sent = false;
            try
            {
                if (message.Kind == MessageKind.Artwork)
                {
                    await this._artworkAdmission.WaitAsync(admissionDeadline.Token).ConfigureAwait(false);
                    artworkAdmitted = true;
                }
                await this._admission.WaitAsync(admissionDeadline.Token).ConfigureAwait(false);
                admitted = true;
                id = Interlocked.Increment(ref this._nextRequestId);
                var pending = new PendingRequest(message, deadline.Token);
                this._pending.TryAdd(id, pending);
                try
                {
                    await this.Protocol.WriteAsync(message with { RequestId = id }, admissionDeadline.Token, timeout.Token).ConfigureAwait(false);
                    sent = true;
                    if (!duringStartup) { timeout.CancelAfter(budget); }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    this.Close();
                    throw new IOException("The worker connection closed while sending a request.", ex);
                }
                var reply = await pending.Completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                return reply.Error is null ? reply : throw new IOException(reply.Error);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !this._closed.IsCancellationRequested)
            {
                if (!sent && !this.Protocol.IsClosed) { throw new TimeoutException("The worker request timed out before it was sent."); }
                this.Close();
                throw new TimeoutException("The worker request timed out; its outcome may be unknown.");
            }
            finally
            {
                if (!sent && admitted && this._pending.TryRemove(id, out var abandoned))
                {
                    this.ReleaseAdmission(abandoned);
                }
                else if (!admitted && artworkAdmitted) { this._artworkAdmission.Release(); }

                if (sent && cancellationToken.IsCancellationRequested)
                {
                    _ = this.CancelRequestAsync(id);
                }

                lock (this._requestGate)
                {
                    if (--this._active == 0 && this._closing)
                    {
                        this._drained.TrySetResult();
                    }
                }
            }
        }

        public async Task ReadLoopAsync(Func<MediaBackendSnapshot, bool> acceptSnapshot, Action<WireMessage> acceptSnapshotFailure,
            Func<HostedSourceActivation, CancellationToken, Task<bool>> activate)
        {
            try
            {
                while (true)
                {
                    var message = await this.Protocol.ReadAsync(this._closed.Token).ConfigureAwait(false);
                    if (message.Version != PipeProtocol.Version || message.Owner != this.Owner || message.Epoch != this.Epoch)
                    {
                        throw new InvalidDataException("The reply belongs to a different worker lifetime.");
                    }

                    switch (message.Kind)
                    {
                        case MessageKind.Snapshot when message.Snapshot is { } snapshot:
                            if (acceptSnapshot(snapshot))
                            {
                                this.FirstSnapshot.TrySetResult();
                            }

                            break;
                        case MessageKind.Reply:
                            if (this._pending.TryRemove(message.RequestId, out var pending))
                            {
                                this.ReleaseAdmission(pending);
                                if (pending.Cancellation.IsCancellationRequested)
                                {
                                    pending.Completion.TrySetCanceled(pending.Cancellation);
                                }
                                else { pending.Completion.TrySetResult(message); }
                            }

                            break;
                        case MessageKind.ArtworkStart:
                        case MessageKind.ArtworkChunk:
                        case MessageKind.ArtworkEnd:
                            if (this._pending.TryGetValue(message.RequestId, out var artwork))
                            {
                                if (artwork.Cancellation.IsCancellationRequested) { artwork.AbandonArtwork(); }
                                else { artwork.ReceiveArtwork(message); }
                                if (message.Kind == MessageKind.ArtworkEnd && this._pending.TryRemove(message.RequestId, out var completed))
                                {
                                    this.ReleaseAdmission(completed);
                                    if (artwork.Cancellation.IsCancellationRequested) { artwork.Completion.TrySetCanceled(artwork.Cancellation); }
                                }
                            }

                            break;
                        case MessageKind.ActivateSource:
                            if (!this._pending.TryGetValue(message.RequestId, out var command) || command.Cancellation.IsCancellationRequested)
                            {
                                break;
                            }

                            if (message.Activation is { } activation &&
                                activation.CommandId == message.RequestId && command.Message.Command is { Operation: MediaOperation.ActivateSource } target &&
                                activation.Target == new MediaBackendSessionTarget(target.SessionId, target.BindingGeneration) &&
                                Interlocked.Exchange(ref command.CallbackUsed, 1) == 0)
                            {
                                _ = Task.Run(() => this.ReplyActivationAsync(message.RequestId, activation, activate, command.Cancellation), CancellationToken.None);
                            }
                            else
                            {
                                throw new InvalidDataException("Activation does not match an admitted command.");
                            }

                            break;
                        case MessageKind.SnapshotFailure:
                            acceptSnapshotFailure(message);
                            break;
                        case MessageKind.Fault:
                            throw new IOException(message.Error ?? "The worker failed.");
                        default:
                            throw new InvalidDataException("Unexpected worker reply.");
                    }
                }
            }
            catch (Exception ex)
            {
                this.Failure = ex.Message;
                this.FirstSnapshot.TrySetException(ex);
                foreach (var pending in this._pending.Values)
                {
                    if (pending.Cancellation.IsCancellationRequested) { pending.Completion.TrySetCanceled(pending.Cancellation); }
                    else { pending.Completion.TrySetException(new IOException("The worker disconnected; the operation outcome may be unknown.", ex)); }
                }
            }
            finally
            {
                this.Close();
            }
        }

        public void Invalidate(ImmutableArray<MediaBackendObservationRequest> requests)
        {
            lock (this._requestGate)
            {
                if (this._closing)
                {
                    return;
                }

                foreach (var request in requests)
                {
                    this._invalidations[request.SessionId] = this._invalidations.GetValueOrDefault(request.SessionId) | request.Changes;
                }

                if (!this._invalidating)
                {
                    this._invalidating = true;
                    _ = Task.Run(this.SendInvalidationsAsync, CancellationToken.None);
                }
            }
        }

        private async Task SendInvalidationsAsync()
        {
            while (true)
            {
                ImmutableArray<MediaBackendObservationRequest> requests;
                lock (this._requestGate)
                {
                    if (this._closing || this._invalidations.Count == 0)
                    {
                        this._invalidating = false;
                        return;
                    }

                    requests = [.. this._invalidations.Select(static entry => new MediaBackendObservationRequest(entry.Key, entry.Value))];
                    this._invalidations.Clear();
                }

                if (!await OutOfProcessMediaBackend.SendInvalidationsAsync(this, requests).ConfigureAwait(false))
                {
                    lock (this._requestGate)
                    {
                        foreach (var request in requests)
                        {
                            this._invalidations[request.SessionId] = this._invalidations.GetValueOrDefault(request.SessionId) | request.Changes;
                        }
                    }
                }
            }
        }

        private async Task ReplyActivationAsync(long id, HostedSourceActivation activation,
            Func<HostedSourceActivation, CancellationToken, Task<bool>> activate, CancellationToken cancellationToken)
        {
            var activated = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                activated = await activate(activation, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // An obsolete or failed activation cannot succeed its original command.
            }

            try
            {
                await this.Protocol.WriteAsync(new(MessageKind.ActivationReply, this.Owner, this.Epoch, id)
                {
                    Activated = activated,
                }, this._closed.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                this.Close();
            }
        }

        private sealed class PendingRequest(WireMessage message, CancellationToken cancellation)
        {
            private byte[]? _artwork;
            private MediaArtworkContent? _metadata;
            private int _offset;
            private bool _started;
            public WireMessage Message { get; } = message;
            public CancellationToken Cancellation { get; } = cancellation;
            public TaskCompletionSource<WireMessage> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int CallbackUsed;

            public void AbandonArtwork() => this._artwork = null;

            public void ReceiveArtwork(WireMessage frame)
            {
                if (this.Message.Kind != MessageKind.Artwork || this.Completion.Task.IsCompleted)
                {
                    throw new InvalidDataException("Unexpected artwork reply.");
                }

                switch (frame.Kind)
                {
                    case MessageKind.ArtworkStart:
                        if (this._started || frame.ArtworkKey != this.Message.ArtworkKey || frame.ArtworkLength < 0 ||
                            frame.ArtworkLength > PipeProtocol.MaximumArtworkBytes || frame.Artwork is { Data.IsEmpty: false } ||
                            frame.Artwork is null && frame.ArtworkLength != 0)
                        {
                            throw new InvalidDataException("Invalid artwork header.");
                        }

                        this._started = true;
                        this._metadata = frame.Artwork;
                        this._artwork = new byte[frame.ArtworkLength];
                        break;
                    case MessageKind.ArtworkChunk:
                        if (!this._started || frame.Offset != this._offset || frame.Chunk is not { Length: > 0 } chunk ||
                            chunk.Length > this._artwork!.Length - this._offset)
                        {
                            throw new InvalidDataException("Invalid artwork chunk sequence.");
                        }

                        chunk.CopyTo(this._artwork, this._offset);
                        this._offset += chunk.Length;
                        break;
                    case MessageKind.ArtworkEnd:
                        if (!this._started || frame.ArtworkKey != this.Message.ArtworkKey || this._offset != this._artwork!.Length)
                        {
                            throw new InvalidDataException("Incomplete artwork reply.");
                        }

                        this.Completion.TrySetResult(frame with
                        {
                            Kind = MessageKind.Reply,
                            Artwork = this._metadata is null ? null : this._metadata with { Data = this._artwork },
                        });
                        this._artwork = null;
                        break;
                }
            }
        }

        public void Close()
        {
            this.Health.Complete();
            this.Protocol.Dispose();
            lock (this._requestGate)
            {
                if (!this._closing)
                {
                    this._closing = true;
                    try { this._closed.Cancel(); }
                    catch (AggregateException ex) { this.Failure ??= ex.Message; }
                    foreach (var id in this._pending.Keys)
                    {
                        if (this._pending.TryRemove(id, out var pending))
                        {
                            this.ReleaseAdmission(pending);
                            pending.Completion.TrySetCanceled(this._closed.Token);
                        }
                    }
                    if (this._active == 0)
                    {
                        this._drained.TrySetResult();
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            this.Close();
            await this._drained.Task.ConfigureAwait(false);
            this._closed.Dispose();
            this._admission.Dispose();
            this._artworkAdmission.Dispose();
        }

        private void ReleaseAdmission(PendingRequest request)
        {
            this._admission.Release();
            if (request.Message.Kind == MessageKind.Artwork) { this._artworkAdmission.Release(); }
        }

        private async Task CancelRequestAsync(long id)
        {
            try
            {
                await this.Protocol.WriteAsync(new(MessageKind.Cancel, this.Owner, this.Epoch, id), this._closed.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                this.Protocol.Dispose();
            }
        }
    }
}