// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Diagnostics;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using Sample = JPSoftworks.MediaControlsExtension.Media.Gsmtc.GsmtcPlaybackObservations.Sample;

namespace JPSoftworks.MediaControlsExtension.Media.Gsmtc;

/// <summary>Confirms one playback transition per binding within a shared deadline.</summary>
internal sealed class GsmtcPlaybackController
{
    private static readonly TimeSpan ConsecutiveObservationInterval = TimeSpan.FromMilliseconds(50);
    private readonly TimeSpan _transitionTimeout;
    private readonly TimeSpan _fallbackDelay;
    private int _active;

    public GsmtcPlaybackController()
        : this(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(500))
    {
    }

    internal GsmtcPlaybackController(TimeSpan transitionTimeout, TimeSpan fallbackDelay)
    {
        this._transitionTimeout = transitionTimeout;
        this._fallbackDelay = fallbackDelay;
    }

    /// <summary>Revalidates an absolute intent once, then shares event observations with snapshots.</summary>
    public async Task<MediaBackendCommandResult> ExecuteAsync(
        MediaOperation operation,
        GsmtcPlaybackObservations observations,
        Func<Task<PlaybackObservation>> readAsync,
        Func<Action, CancellationToken, Task<PlaybackSendResult>> sendAsync,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref this._active, 1, 0) != 0)
        {
            return new(MediaBackendCommandStatus.Unavailable, "A playback transition is already in progress.");
        }

        var desired = operation == MediaOperation.Play ? MediaPlaybackState.Playing : MediaPlaybackState.Paused;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(this._transitionTimeout);
        var token = deadline.Token;
        var fallbackStartedAt = Stopwatch.GetTimestamp();
        var sendState = 0;

        try
        {
            var fallbackUsed = false;
            var specialReadVersion = -1L;
            var awaitingConfirmation = false;
            var shouldSend = true;
            var cursor = 0L;
            Sample? firstStopOnly = null;
            Sample? firstStopped = null;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                Sample observed;
                if (shouldSend)
                {
                    var result = await AwaitAsync(
                        observations.WithCommandReadAsync(() => sendAsync(MarkSending, token), token), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    observations.EnsureActive();
                    shouldSend = false;
                    observed = result.Observation;
                    cursor = result.DecisionSequence;
                    if (result.Status == PlaybackSendStatus.Rejected)
                    {
                        return new(MediaBackendCommandStatus.Failed, "GSMTC rejected the requested operation.");
                    }

                    if (result.Status == PlaybackSendStatus.Sent)
                    {
                        fallbackStartedAt = Stopwatch.GetTimestamp();
                        awaitingConfirmation = true;
                        firstStopOnly = null;
                        continue;
                    }

                    if (result.Status == PlaybackSendStatus.AlreadySatisfied)
                    {
                        if (observed.Value.State != MediaPlaybackState.Stopped)
                        {
                            return new(MediaBackendCommandStatus.Completed, null);
                        }

                        fallbackStartedAt = Stopwatch.GetTimestamp();
                        awaitingConfirmation = true;
                        firstStopOnly = null;
                        continue;
                    }

                    if (Volatile.Read(ref sendState) == 1)
                    {
                        return new(MediaBackendCommandStatus.Unconfirmed, "Playback changed after a native mutation started.");
                    }
                }
                else
                {
                    var special = firstStopOnly ?? firstStopped;
                    var specialDelay = special is { } first && first.ChangeVersion != specialReadVersion
                        ? RemainingDelay(ConsecutiveObservationInterval, first.CompletedAt)
                        : Timeout.InfiniteTimeSpan;
                    var fallbackDelay = fallbackUsed ? Timeout.InfiniteTimeSpan : RemainingDelay(this._fallbackDelay, fallbackStartedAt);
                    var useSpecial = specialDelay != Timeout.InfiniteTimeSpan &&
                                     (fallbackDelay == Timeout.InfiniteTimeSpan || specialDelay <= fallbackDelay);
                    var delay = useSpecial ? specialDelay : fallbackDelay;
                    var next = await WaitForObservationAsync(observations, cursor, readAsync, delay, token).ConfigureAwait(false);
                    if (next is { } published)
                    {
                        observed = published;
                    }
                    else if (useSpecial)
                    {
                        specialReadVersion = special!.Value.ChangeVersion;
                        var notBefore = special.Value.CompletedAt + (long)(ConsecutiveObservationInterval.TotalSeconds * Stopwatch.Frequency);
                        observed = await observations.ReadFreshAsync(readAsync, cursor, notBefore, token).ConfigureAwait(false);
                    }
                    else
                    {
                        fallbackUsed = true;
                        if (!awaitingConfirmation && firstStopOnly is null)
                        {
                            shouldSend = true;
                            continue;
                        }

                        observed = await observations.ReadFreshAsync(readAsync, cursor, 0, token).ConfigureAwait(false);
                    }

                    token.ThrowIfCancellationRequested();
                    observations.EnsureActive();
                    cursor = observed.Sequence;
                    if (awaitingConfirmation)
                    {
                        if (observed.Value.State == desired)
                        {
                            return new(MediaBackendCommandStatus.Completed, null);
                        }

                        if (operation == MediaOperation.Pause && observed.Value.State == MediaPlaybackState.Stopped)
                        {
                            if (IsConsecutive(firstStopped, observed, stopped: true))
                            {
                                return new(MediaBackendCommandStatus.Completed, null);
                            }

                            firstStopped = FirstInRun(firstStopped, observed, stopped: true);
                            continue;
                        }

                        firstStopped = null;
                        if (Volatile.Read(ref sendState) != 0)
                        {
                            continue;
                        }

                        awaitingConfirmation = false;
                    }

                    if (TryResolveNativeOperation(operation, observed.Value, out _))
                    {
                        if (next is null && fallbackUsed)
                        {
                            continue;
                        }

                        // A forced readiness observation spends the remaining fallback on revalidation.
                        fallbackUsed |= next is null;
                        shouldSend = true;
                        firstStopOnly = null;
                        continue;
                    }
                }

                if (IsStopOnlyPause(operation, observed.Value))
                {
                    if (IsConsecutive(firstStopOnly, observed, stopped: false))
                    {
                        return new(MediaBackendCommandStatus.Unsupported, "The playing session supports Stop but not Pause.");
                    }

                    firstStopOnly = FirstInRun(firstStopOnly, observed, stopped: false);
                }
                else
                {
                    firstStopOnly = null;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var wasSent = Interlocked.CompareExchange(ref sendState, 2, 0) == 1;
            return new(wasSent ? MediaBackendCommandStatus.Unconfirmed : MediaBackendCommandStatus.Unavailable,
                wasSent ? "Playback did not reach the requested state before the transition deadline."
                    : "Playback was not ready before the transition deadline; no command was sent.");
        }
        catch (GsmtcSessionRetiredException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(MediaBackendCommandStatus.SessionGone, ex.Message);
        }
        catch (Exception ex) when (Volatile.Read(ref sendState) == 1 && ex is not OperationCanceledException &&
                                   !GsmtcErrors.IndicatesStaleSession(ex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(MediaBackendCommandStatus.Unconfirmed, ex.Message);
        }
        finally
        {
            Interlocked.CompareExchange(ref sendState, 2, 0);
            Volatile.Write(ref this._active, 0);
        }

        void MarkSending()
        {
            token.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref sendState, 1, 0) == 2)
            {
                throw new OperationCanceledException(token);
            }
        }
    }

    private static TimeSpan RemainingDelay(TimeSpan interval, long startedAt)
    {
        var remaining = interval - Stopwatch.GetElapsedTime(startedAt);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static Sample FirstInRun(Sample? first, Sample observed, bool stopped) =>
        first is { } previous && (stopped ? previous.StoppedRun == observed.StoppedRun : previous.StopOnlyRun == observed.StopOnlyRun)
            ? previous : observed;

    private static bool IsConsecutive(Sample? first, Sample observed, bool stopped) =>
        first is { } previous && previous.Sequence != observed.Sequence &&
        (stopped ? previous.StoppedRun == observed.StoppedRun : previous.StopOnlyRun == observed.StopOnlyRun) &&
        Stopwatch.GetElapsedTime(previous.CompletedAt, observed.StartedAt) >= ConsecutiveObservationInterval;

    private static async Task<Sample?> WaitForObservationAsync(
        GsmtcPlaybackObservations observations, long cursor, Func<Task<PlaybackObservation>> readAsync,
        TimeSpan delay, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var next = observations.WaitForObservationAsync(cursor, readAsync, wait.Token);
        if (delay == Timeout.InfiniteTimeSpan)
        {
            return await next.ConfigureAwait(false);
        }

        var timer = Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, wait.Token);
        try
        {
            await Task.WhenAny(next, timer).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return next.IsCompleted ? await next.ConfigureAwait(false) : null;
        }
        finally
        {
            wait.Cancel();
            await ((Task)next).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>Revalidates playback state and controls under the native control gate.</summary>
    internal static async Task<PlaybackSendResult> SendRevalidatedAsync(
        MediaOperation intent,
        GsmtcPlaybackObservations observations,
        Sample observed,
        Func<MediaOperation, Task<bool>> sendAsync,
        Action markSending)
    {
        var sequence = observations.CaptureSequence();
        if (IsStopOnlyPause(intent, observed.Value))
        {
            return new(PlaybackSendStatus.StopOnly, observed, sequence);
        }

        if (!TryResolveNativeOperation(intent, observed.Value, out var operation))
        {
            return new(PlaybackSendStatus.NotReady, observed, sequence);
        }

        if (operation is null)
        {
            return new(PlaybackSendStatus.AlreadySatisfied, observed, sequence);
        }

        markSending();
        var accepted = await sendAsync(operation.Value).ConfigureAwait(false);
        return new(accepted ? PlaybackSendStatus.Sent : PlaybackSendStatus.Rejected, observed, sequence);
    }

    private static bool TryResolveNativeOperation(
        MediaOperation intent,
        PlaybackObservation observed,
        out MediaOperation? operation)
    {
        operation = null;
        if (intent == MediaOperation.Pause && observed.State == MediaPlaybackState.Stopped)
        {
            return true;
        }

        var desired = intent == MediaOperation.Play ? MediaPlaybackState.Playing : MediaPlaybackState.Paused;
        var capability = intent == MediaOperation.Play ? MediaCapabilities.Play : MediaCapabilities.Pause;
        if (observed.Capabilities.HasFlag(capability))
        {
            operation = intent;
            return true;
        }

        if (observed.State == desired)
        {
            return true;
        }

        if (observed.Capabilities.HasFlag(MediaCapabilities.TogglePlayback) &&
            observed.State is MediaPlaybackState.Playing or MediaPlaybackState.Paused)
        {
            operation = MediaOperation.TogglePlayback;
            return true;
        }

        return false;
    }

    internal static bool IsStopOnlyPause(MediaOperation intent, PlaybackObservation observed) =>
        intent == MediaOperation.Pause && observed.State == MediaPlaybackState.Playing &&
        observed.Capabilities.HasFlag(MediaCapabilities.Stop) &&
        !observed.Capabilities.HasFlag(MediaCapabilities.Pause) &&
        !observed.Capabilities.HasFlag(MediaCapabilities.TogglePlayback);

    private static async Task<T> AwaitAsync<T>(Task<T> task, CancellationToken cancellationToken)
    {
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!task.IsCompletedSuccessfully)
            {
                _ = ObserveCompletionAsync(task);
            }
        }
    }

    private static async Task ObserveCompletionAsync(Task task) =>
        await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    internal enum PlaybackSendStatus { Sent, AlreadySatisfied, NotReady, StopOnly, Rejected }

    internal readonly record struct PlaybackSendResult(PlaybackSendStatus Status, Sample Observation, long DecisionSequence);

    internal readonly record struct PlaybackObservation(MediaPlaybackState State, MediaCapabilities Capabilities);
}