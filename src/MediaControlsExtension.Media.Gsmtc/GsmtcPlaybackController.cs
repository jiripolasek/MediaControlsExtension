// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.MediaControlsExtension.Media.Gsmtc;

/// <summary>Confirms one playback transition per binding within a shared deadline.</summary>
internal sealed class GsmtcPlaybackController
{
    private readonly TimeSpan _transitionTimeout;
    private readonly TimeSpan _pollInterval;
    private readonly GsmtcObservationGate _observations;
    private int _active;

    public GsmtcPlaybackController(ILogger? logger = null)
        : this(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(50), logger)
    {
    }

    internal GsmtcPlaybackController(TimeSpan transitionTimeout, TimeSpan pollInterval, ILogger? logger = null)
    {
        this._transitionTimeout = transitionTimeout;
        this._pollInterval = pollInterval;
        this._observations = new(logger ?? NullLogger.Instance, transitionTimeout, TimeSpan.Zero);
    }

    /// <summary>Sends an absolute intent once, then waits for bounded playback confirmation.</summary>
    /// <remarks>The send operation is a readiness hint; revalidate it and mark native mutations before starting them.</remarks>
    public async Task<MediaBackendCommandResult> ExecuteAsync(
        MediaOperation operation,
        Func<CancellationToken, Task<PlaybackObservation>> readAsync,
        Func<MediaOperation?, Action, CancellationToken, Task<bool>> sendAsync,
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
        var sendState = 0;
        void MarkSending()
        {
            token.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref sendState, 1, 0) == 2)
            {
                throw new OperationCanceledException(token);
            }
        }

        try
        {
            var stopOnlyObserved = false;
            var stoppedObserved = false;
            var awaitingConfirmation = false;
            while (true)
            {
                var observed = await this.ReadAsync(readAsync, token).ConfigureAwait(false);
                if (awaitingConfirmation)
                {
                    var stopped = operation == MediaOperation.Pause && observed.State == MediaPlaybackState.Stopped;
                    if (observed.State == desired || (stopped && stoppedObserved))
                    {
                        return new(MediaBackendCommandStatus.Completed, null);
                    }

                    if (Volatile.Read(ref sendState) != 0 || stopped)
                    {
                        stoppedObserved = stopped;
                        await Task.Delay(this._pollInterval, token).ConfigureAwait(false);
                        continue;
                    }

                    awaitingConfirmation = false;
                    stoppedObserved = false;
                }

                var stopOnly = IsStopOnlyPause(operation, observed);
                if (stopOnly && stopOnlyObserved)
                {
                    throw new StopOnlyPlaybackException();
                }

                stopOnlyObserved = stopOnly;
                if (!TryResolveNativeOperation(operation, observed, out var nativeOperation))
                {
                    await Task.Delay(this._pollInterval, token).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    if (!await AwaitAsync(sendAsync(nativeOperation, MarkSending, token), token).ConfigureAwait(false))
                    {
                        return Volatile.Read(ref sendState) == 1
                            ? new(MediaBackendCommandStatus.Failed, "GSMTC rejected the requested operation.")
                            : new(MediaBackendCommandStatus.Unavailable, "Playback changed before the requested operation could be sent.");
                    }

                    awaitingConfirmation = true;
                }
                catch (StopOnlyPlaybackException) when (Volatile.Read(ref sendState) == 0)
                {
                    // Recheck after releasing the control gate; no native mutation has started.
                    stopOnlyObserved = true;
                    await Task.Delay(this._pollInterval, token).ConfigureAwait(false);
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
        catch (Exception ex) when (Volatile.Read(ref sendState) == 1 && ex is not OperationCanceledException &&
                                   ex is not GsmtcSessionRetiredException && !GsmtcErrors.IndicatesStaleSession(ex))
        {
            return new(MediaBackendCommandStatus.Unconfirmed, ex.Message);
        }
        catch (GsmtcObservationBlockedException ex)
        {
            return new(MediaBackendCommandStatus.Unavailable, ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return new(MediaBackendCommandStatus.Unsupported, ex.Message);
        }
        finally
        {
            Interlocked.CompareExchange(ref sendState, 2, 0);
            Volatile.Write(ref this._active, 0);
        }
    }

    private Task<PlaybackObservation> ReadAsync(
        Func<CancellationToken, Task<PlaybackObservation>> readAsync, CancellationToken cancellationToken) =>
        AwaitAsync(this._observations.RunAsync(() => readAsync(cancellationToken), "ConfirmPlayback", cancellationToken), cancellationToken);

    /// <summary>Revalidates playback state and controls under the native control gate.</summary>
    internal static Task<bool> SendRevalidatedAsync(
        MediaOperation intent,
        PlaybackObservation observed,
        Func<MediaOperation, Task<bool>> sendAsync,
        Action markSending)
    {
        if (IsStopOnlyPause(intent, observed))
        {
            throw new StopOnlyPlaybackException();
        }

        if (!TryResolveNativeOperation(intent, observed, out var operation))
        {
            return Task.FromResult(false);
        }

        if (operation is null)
        {
            return Task.FromResult(true);
        }

        markSending();
        return sendAsync(operation.Value);
    }

    private static bool TryResolveNativeOperation(
        MediaOperation intent, PlaybackObservation observed, out MediaOperation? operation)
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

    private static bool IsStopOnlyPause(MediaOperation intent, PlaybackObservation observed) =>
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

    private sealed class StopOnlyPlaybackException() : NotSupportedException("The playing session supports Stop but not Pause.");

    internal readonly record struct PlaybackObservation(MediaPlaybackState State, MediaCapabilities Capabilities);
}