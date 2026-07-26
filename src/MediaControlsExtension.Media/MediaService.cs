// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using System.Threading.Channels;
using JPSoftworks.MediaControlsExtension.Media.Diagnostics;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure.Gsmtc;
using JPSoftworks.MediaControlsExtension.Media.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.MediaControlsExtension.Media;

public sealed class MediaService : IMediaService
{
    private static readonly TimeSpan NavigationCommandInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PlaybackCommandInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan NavigationSettleRefreshDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PlaybackPredictionLifetime = TimeSpan.FromSeconds(10);

    private readonly IMediaBackend _backend;
    private readonly Lock _commandAdmissionLock = new();
    private readonly Channel<CommandWork> _commandQueue;
    private readonly Channel<bool> _refreshRequests;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Lock _lifecycleLock = new();
    private readonly Dictionary<MediaSessionId, long> _lastNavigationCommandTimestamps = [];
    private readonly Dictionary<MediaSessionId, long> _lastPlaybackCommandTimestamps = [];
    private readonly ILogger _logger;
    private readonly Timer _navigationSettleRefreshTimer;
    private readonly MediaNotificationHub _notificationHub;
    private readonly MediaSessionCatalog _sessionCatalog = new();
    private readonly MediaStateStore _stateStore = new();
    private readonly TaskCompletionSource _startCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeProvider _timeProvider;
    private readonly Task _commandPumpTask;
    private readonly TimeSpan _playbackPredictionLifetime;

    private Task? _backendSignalPumpTask;
    private Task? _disposeTask;
    private Task? _refreshPumpTask;
    private long _nextOperationId;
    private int _startState;
    private int _disposeState;

    public MediaService(ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        this._logger = loggerFactory.CreateLogger<MediaService>();
        this._backend = new GsmtcBackend(loggerFactory.CreateLogger<GsmtcBackend>());
        this._commandQueue = CreateCommandQueue();
        this._refreshRequests = CreateRefreshQueue();
        this._timeProvider = TimeProvider.System;
        this._playbackPredictionLifetime = PlaybackPredictionLifetime;
        this._navigationSettleRefreshTimer = this.CreateNavigationSettleRefreshTimer();
        this._notificationHub = new(this.RaiseChanged, this._logger);
        this._commandPumpTask = Task.Run(this.ProcessCommandsAsync);
    }

    internal MediaService(
        IMediaBackend backend,
        ILogger<MediaService>? logger = null,
        TimeProvider? timeProvider = null,
        TimeSpan? playbackPredictionLifetime = null)
    {
        this._backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this._logger = logger ?? NullLogger<MediaService>.Instance;
        this._commandQueue = CreateCommandQueue();
        this._refreshRequests = CreateRefreshQueue();
        this._timeProvider = timeProvider ?? TimeProvider.System;
        this._playbackPredictionLifetime =
            playbackPredictionLifetime ?? PlaybackPredictionLifetime;
        if (this._playbackPredictionLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(playbackPredictionLifetime),
                this._playbackPredictionLifetime,
                "The playback prediction lifetime must be positive.");
        }

        this._navigationSettleRefreshTimer = this.CreateNavigationSettleRefreshTimer();
        this._notificationHub = new(this.RaiseChanged, this._logger);
        this._commandPumpTask = Task.Run(this.ProcessCommandsAsync);
    }

    public event EventHandler? SessionsChanged;

    public event EventHandler? CurrentSessionChanged;

    public event EventHandler? StatusChanged;

    public ImmutableArray<MediaSession> Sessions => this._sessionCatalog.State.Sessions;

    public MediaSession? CurrentSession => this._sessionCatalog.State.CurrentSession;

    public MediaServiceStatus Status => this._sessionCatalog.State.Status;

    public MediaControlAvailability Availability => this._sessionCatalog.State.Availability;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var startsService = false;
        lock (this._lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(this._disposeState != 0, this);
            if (this._startState == 0)
            {
                this._startState = 1;
                startsService = true;
                var startingSnapshot = this._stateStore.SetStatus(
                    MediaServiceStatus.Starting,
                    MediaControlAvailability.Unavailable);
                this.PublishStateCore(startingSnapshot);
                MediaLog.ServiceStarting(this._logger);
            }
        }

        if (!startsService)
        {
            await this._startCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        using var startCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            this._disposeCts.Token);
        var startToken = startCts.Token;
        try
        {
            await this._backend.StartAsync(startToken).ConfigureAwait(false);
            startToken.ThrowIfCancellationRequested();
            await this.RefreshSnapshotAsync(startToken).ConfigureAwait(false);
            startToken.ThrowIfCancellationRequested();

            lock (this._lifecycleLock)
            {
                startToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(this._disposeState != 0, this);

                var disposeToken = this._disposeCts.Token;
                this._refreshPumpTask = Task.Run(
                    () => this.ProcessRefreshRequestsAsync(disposeToken),
                    CancellationToken.None);
                this._backendSignalPumpTask = Task.Run(
                    () => this.ProcessBackendSignalsAsync(disposeToken),
                    CancellationToken.None);
                this.RequestRefresh();

                this._startState = 2;
                MediaLog.ServiceReady(this._logger, this.Sessions.Length);
                this._startCompletion.TrySetResult();
            }
        }
        catch (Exception ex)
        {
            var disposedDuringStart = false;
            lock (this._lifecycleLock)
            {
                this._startState = 3;
                disposedDuringStart = this._disposeState != 0;
                if (disposedDuringStart)
                {
                    this._startCompletion.TrySetCanceled(this._disposeCts.Token);
                }
                else
                {
                    var failedSnapshot = this._stateStore.SetStatus(
                        MediaServiceStatus.Faulted,
                        MediaControlAvailability.Unavailable);
                    this.PublishStateCore(failedSnapshot);
                    MediaLog.ServiceStartFailed(this._logger, ex);
                    this._startCompletion.TrySetException(ex);
                }
            }

            if (disposedDuringStart)
            {
                throw new OperationCanceledException(
                    "Media service startup was canceled because the service is being disposed.",
                    ex,
                    this._disposeCts.Token);
            }

            throw;
        }
    }

    public MediaCommandSubmission TrySubmit(MediaCommand command)
    {
        if (Volatile.Read(ref this._disposeState) != 0)
        {
            return Rejected(MediaCommandSubmissionStatus.NotReady, this._stateStore.Current.Revision);
        }

        var operationId = default(MediaOperationId);
        var resolvedCommand = default(ResolvedMediaCommand);
        MediaServiceSnapshot? predictedSnapshot = null;
        CommandWork? work = null;
        var wasThrottled = false;
        var mailboxWasFull = false;
        var rejectionStatus = MediaCommandSubmissionStatus.Accepted;
        lock (this._commandAdmissionLock)
        {
            rejectionStatus = this._stateStore.TryResolveCommand(command, out resolvedCommand);
            if (rejectionStatus == MediaCommandSubmissionStatus.Accepted)
            {
                var isNavigationCommand = IsNavigationCommand(resolvedCommand.ResolvedOperation);
                var isPlaybackCommand = IsPlaybackInputCommand(resolvedCommand.RequestedOperation);
                var admissionTimestamp = this._timeProvider.GetTimestamp();
                if (isNavigationCommand)
                {
                    wasThrottled = WasRecentlyAdmitted(
                        this._lastNavigationCommandTimestamps,
                        resolvedCommand.SessionId,
                        admissionTimestamp,
                        NavigationCommandInterval);
                }
                else if (isPlaybackCommand)
                {
                    wasThrottled = WasRecentlyAdmitted(
                        this._lastPlaybackCommandTimestamps,
                        resolvedCommand.SessionId,
                        admissionTimestamp,
                        PlaybackCommandInterval);
                }

                if (!wasThrottled)
                {
                    operationId = new(Interlocked.Increment(ref this._nextOperationId));
                    work = new(operationId, resolvedCommand);
                    if (!this._commandQueue.Writer.TryWrite(work))
                    {
                        work = null;
                        mailboxWasFull = true;
                    }

                    if (work is not null)
                    {
                        if (isNavigationCommand)
                        {
                            this._lastNavigationCommandTimestamps[resolvedCommand.SessionId] =
                                admissionTimestamp;
                        }
                        else if (isPlaybackCommand)
                        {
                            this._lastPlaybackCommandTimestamps[resolvedCommand.SessionId] =
                                admissionTimestamp;
                        }

                        predictedSnapshot = this._stateStore.ApplyPrediction(
                            resolvedCommand,
                            operationId);
                    }
                }
            }
        }

        if (rejectionStatus != MediaCommandSubmissionStatus.Accepted)
        {
            MediaLog.CommandRejected(this._logger, command.Operation, rejectionStatus);
            return Rejected(rejectionStatus, this._stateStore.Current.Revision);
        }

        if (work is null)
        {
            if (wasThrottled)
            {
                MediaLog.CommandThrottled(
                    this._logger,
                    resolvedCommand.ResolvedOperation,
                    resolvedCommand.SessionId.Value);
            }
            else if (mailboxWasFull)
            {
                MediaLog.CommandMailboxFull(this._logger, resolvedCommand.ResolvedOperation);
            }

            return Rejected(
                MediaCommandSubmissionStatus.Busy,
                this._stateStore.Current.Revision);
        }

        ArgumentNullException.ThrowIfNull(predictedSnapshot);
        this.PublishState(predictedSnapshot);
        work.AllowExecution();
        _ = this.ExpirePredictionAsync(resolvedCommand.SessionId, operationId);

        MediaLog.CommandAccepted(
            this._logger,
            operationId.Value,
            resolvedCommand.ResolvedOperation,
            resolvedCommand.SessionId.Value);
        return new(
            MediaCommandSubmissionStatus.Accepted,
            operationId,
            predictedSnapshot.Revision,
            work.Completion);
    }

    public ValueTask<MediaArtworkContent?> GetArtworkAsync(
        MediaArtworkKey key,
        CancellationToken cancellationToken = default)
    {
        return this._backend.GetArtworkAsync(key, cancellationToken);
    }

    public void UpdateOptions(MediaServiceOptions options)
    {
        this._stateStore.UpdateOptions(options);
    }

    public void Dispose()
    {
        _ = this.EnsureDisposeStarted();
    }

    public async ValueTask DisposeAsync()
    {
        await this.EnsureDisposeStarted().ConfigureAwait(false);
    }

    private static Channel<CommandWork> CreateCommandQueue()
    {
        return Channel.CreateBounded<CommandWork>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
    }

    private static Channel<bool> CreateRefreshQueue()
    {
        return Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false,
        });
    }

    private static MediaCommandSubmission Rejected(
        MediaCommandSubmissionStatus status,
        long revision)
    {
        return new(status, default, revision, null);
    }

    private async Task ProcessCommandsAsync()
    {
        var cancellationToken = this._disposeCts.Token;
        try
        {
            await foreach (var work in this._commandQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await work.WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
                await this.ExecuteCommandAsync(work, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            while (this._commandQueue.Reader.TryRead(out var pendingWork))
            {
                pendingWork.Cancel();
            }
        }
    }

    private async Task ExecuteCommandAsync(CommandWork work, CancellationToken cancellationToken)
    {
        var command = work.Command;
        MediaBackendCommandResult result;
        try
        {
            result = await this._backend.ExecuteAsync(
                new(
                    command.BackendSessionId,
                    command.BindingGeneration,
                    command.ResolvedOperation,
                    command.SessionsToPause),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            work.Cancel();
            return;
        }
        catch (Exception ex)
        {
            result = new(MediaBackendCommandStatus.Failed, ex.Message);
        }

        var outcomeStatus = result.Status switch
        {
            MediaBackendCommandStatus.Completed => MediaCommandOutcomeStatus.Completed,
            MediaBackendCommandStatus.Unavailable => MediaCommandOutcomeStatus.Unavailable,
            MediaBackendCommandStatus.Unsupported => MediaCommandOutcomeStatus.Unsupported,
            MediaBackendCommandStatus.SessionGone => MediaCommandOutcomeStatus.SessionGone,
            _ => MediaCommandOutcomeStatus.Failed,
        };
        var succeeded = outcomeStatus == MediaCommandOutcomeStatus.Completed;
        var updatedSnapshot = this._stateStore.CompleteCommand(
            command,
            work.OperationId,
            succeeded);
        this.PublishState(updatedSnapshot);
        work.Complete(new(
            work.OperationId,
            outcomeStatus,
            command.SessionId,
            result.DiagnosticMessage));

        if (!succeeded)
        {
            MediaLog.CommandFailed(
                this._logger,
                work.OperationId.Value,
                command.ResolvedOperation,
                command.SessionId.Value,
                result.DiagnosticMessage ?? outcomeStatus.ToString());
        }
        else if (IsNavigationCommand(command.ResolvedOperation))
        {
            this.ScheduleNavigationSettleRefresh();
        }

        this.RequestRefresh();
    }

    private async Task ProcessBackendSignalsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in this._backend.WatchAsync(cancellationToken).ConfigureAwait(false))
            {
                this.RequestRefresh();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            MediaLog.SnapshotRefreshFailed(this._logger, ex);
            var degradedSnapshot = this._stateStore.SetStatus(
                MediaServiceStatus.Degraded,
                MediaControlAvailability.Unavailable);
            this.PublishState(degradedSnapshot);
        }
    }

    private async Task ProcessRefreshRequestsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in this._refreshRequests.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await this.RefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            var backendSnapshot = await this._backend.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = this._stateStore.ApplyBackendSnapshot(backendSnapshot);
            this.PublishState(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            MediaLog.SnapshotRefreshFailed(this._logger, ex);
            var degradedSnapshot = this._stateStore.SetStatus(
                MediaServiceStatus.Degraded,
                MediaControlAvailability.Unavailable);
            this.PublishState(degradedSnapshot);
        }
    }

    private void RequestRefresh()
    {
        this._refreshRequests.Writer.TryWrite(true);
    }

    private Timer CreateNavigationSettleRefreshTimer()
    {
        return new(
            static state => ((MediaService)state!).RequestRefreshIfActive(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    private static bool IsNavigationCommand(MediaOperation operation)
    {
        return operation is MediaOperation.SkipNext or MediaOperation.SkipPrevious;
    }

    private static bool IsPlaybackInputCommand(MediaOperation operation)
    {
        return operation is
            MediaOperation.Play or
            MediaOperation.Pause or
            MediaOperation.Stop or
            MediaOperation.TogglePlayback;
    }

    private bool WasRecentlyAdmitted(
        Dictionary<MediaSessionId, long> timestamps,
        MediaSessionId sessionId,
        long timestamp,
        TimeSpan interval)
    {
        return timestamps.TryGetValue(sessionId, out var previousTimestamp) &&
               this._timeProvider.GetElapsedTime(previousTimestamp, timestamp) < interval;
    }

    private void ScheduleNavigationSettleRefresh()
    {
        if (Volatile.Read(ref this._disposeState) != 0)
        {
            return;
        }

        try
        {
            this._navigationSettleRefreshTimer.Change(
                NavigationSettleRefreshDelay,
                Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref this._disposeState) != 0)
        {
        }
    }

    private void RequestRefreshIfActive()
    {
        if (Volatile.Read(ref this._disposeState) == 0)
        {
            this._backend.InvalidateObservations();
            this.RequestRefresh();
        }
    }

    private async Task ExpirePredictionAsync(
        MediaSessionId sessionId,
        MediaOperationId operationId)
    {
        try
        {
            await Task.Delay(
                this._playbackPredictionLifetime,
                this._disposeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (this._disposeCts.IsCancellationRequested)
        {
            return;
        }

        if (this._stateStore.TryExpirePrediction(sessionId, operationId, out var snapshot))
        {
            this.PublishState(snapshot);
            this.RequestRefresh();
        }
    }

    private Task EnsureDisposeStarted()
    {
        lock (this._lifecycleLock)
        {
            if (this._disposeTask is null)
            {
                this._disposeState = 1;
                this._disposeTask = this.DisposeCoreAsync();
            }

            return this._disposeTask;
        }
    }

    private async Task DisposeCoreAsync()
    {
        var stoppedSnapshot = this._stateStore.SetStatus(
            MediaServiceStatus.Stopped,
            MediaControlAvailability.Unavailable);
        this.PublishStateCore(stoppedSnapshot);
        this._disposeCts.Cancel();
        this._commandQueue.Writer.TryComplete();
        this._refreshRequests.Writer.TryComplete();
        await this._navigationSettleRefreshTimer.DisposeAsync().ConfigureAwait(false);

        if (Volatile.Read(ref this._startState) == 1)
        {
            await AwaitCompletionAsync(this._startCompletion.Task).ConfigureAwait(false);
        }

        await AwaitPumpAsync(this._commandPumpTask).ConfigureAwait(false);
        if (this._backendSignalPumpTask is not null)
        {
            await AwaitPumpAsync(this._backendSignalPumpTask).ConfigureAwait(false);
        }

        if (this._refreshPumpTask is not null)
        {
            await AwaitPumpAsync(this._refreshPumpTask).ConfigureAwait(false);
        }

        await this._backend.DisposeAsync().ConfigureAwait(false);
        await this._notificationHub.CompleteAsync().ConfigureAwait(false);
        this._disposeCts.Dispose();
    }

    private void PublishState(MediaServiceSnapshot snapshot)
    {
        lock (this._lifecycleLock)
        {
            if (this._disposeState != 0)
            {
                return;
            }

            this.PublishStateCore(snapshot);
        }
    }

    private void PublishStateCore(MediaServiceSnapshot snapshot)
    {
        var publication = this._sessionCatalog.Apply(snapshot);
        this._notificationHub.Publish(publication);
    }

    private void RaiseChanged(
        MediaServiceChanges changes,
        Action<Exception> reportException)
    {
        if (changes == MediaServiceChanges.None)
        {
            return;
        }

        if ((changes & MediaServiceChanges.Sessions) != 0)
        {
            this.RaiseEvent(this.SessionsChanged, reportException);
        }

        if (changes.HasFlag(MediaServiceChanges.CurrentSession))
        {
            this.RaiseEvent(this.CurrentSessionChanged, reportException);
        }

        if ((changes & (MediaServiceChanges.Status | MediaServiceChanges.Availability)) != 0)
        {
            this.RaiseEvent(this.StatusChanged, reportException);
        }
    }

    private void RaiseEvent(
        EventHandler? handler,
        Action<Exception> reportException)
    {
        if (handler is null)
        {
            return;
        }

        foreach (EventHandler subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                reportException(ex);
            }
        }
    }

    private static async Task AwaitPumpAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task AwaitCompletionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    internal sealed class CommandWork(
        MediaOperationId operationId,
        ResolvedMediaCommand command)
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<MediaCommandOutcome> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public MediaOperationId OperationId { get; } = operationId;

        public ResolvedMediaCommand Command { get; } = command;

        public Task<MediaCommandOutcome> Completion => this._completion.Task;

        public void AllowExecution() => this._ready.TrySetResult();

        public async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
        {
            try
            {
                await this._ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                this.Cancel();
                throw;
            }
        }

        public void Complete(MediaCommandOutcome outcome) => this._completion.TrySetResult(outcome);

        public void Cancel()
        {
            this._ready.TrySetCanceled();
            this._completion.TrySetResult(new(
                this.OperationId,
                MediaCommandOutcomeStatus.Canceled,
                this.Command.SessionId,
                "The media service was stopped."));
        }
    }
}