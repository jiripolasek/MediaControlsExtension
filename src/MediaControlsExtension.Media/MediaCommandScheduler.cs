// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media;

/// <summary>Orders commands sharing session bindings while allowing independent commands to progress.</summary>
internal sealed class MediaCommandScheduler(
    Func<MediaService.CommandWork, CancellationToken, Task> execute,
    CancellationToken cancellationToken)
{
    internal const int MaximumOutstandingCommands = 64;
    private const int MaximumCommandsPerBinding = 2;

    private readonly Lock _stateLock = new();
    private readonly Dictionary<MediaBackendSessionTarget, Task> _tails = [];
    private readonly Dictionary<MediaBackendSessionTarget, int> _primaryCounts = [];
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _outstandingCount;
    private bool _completed;

    public bool TrySchedule(MediaService.CommandWork work)
    {
        var command = work.Command;
        var primary = new MediaBackendSessionTarget(command.BackendSessionId, command.BindingGeneration);
        lock (this._stateLock)
        {
            var primaryCount = this._primaryCounts.GetValueOrDefault(primary);
            if (this._completed || cancellationToken.IsCancellationRequested ||
                this._outstandingCount >= MaximumOutstandingCommands || primaryCount >= MaximumCommandsPerBinding)
            {
                return false;
            }

            var bindings = command.SessionsToPause.Prepend(primary).Distinct().ToArray();
            var predecessors = bindings.Select(binding => this._tails.GetValueOrDefault(binding)).OfType<Task>().Distinct().ToArray();
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach (var binding in bindings)
            {
                this._tails[binding] = finished.Task;
            }

            this._primaryCounts[primary] = primaryCount + 1;
            this._outstandingCount++;
            _ = Task.Run(() => this.RunAsync(work, primary, bindings, predecessors, finished));
            return true;
        }
    }

    /// <summary>Stops admission and drains accepted work; the owner cancels the lifetime token.</summary>
    public Task CompleteAsync()
    {
        lock (this._stateLock)
        {
            this._completed = true;
            if (this._outstandingCount == 0)
            {
                this._drained.TrySetResult();
            }

            return this._drained.Task;
        }
    }

    private async Task RunAsync(
        MediaService.CommandWork work,
        MediaBackendSessionTarget primary,
        MediaBackendSessionTarget[] bindings,
        Task[] predecessors,
        TaskCompletionSource finished)
    {
        try
        {
            using var cancellation = cancellationToken.Register(static state => ((MediaService.CommandWork)state!).Cancel(), work);
            await Task.WhenAll(predecessors).WaitAsync(cancellationToken).ConfigureAwait(false);
            await work.WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await execute(work, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            work.Cancel();
        }
        catch (Exception ex)
        {
            work.Complete(new(work.OperationId, MediaCommandOutcomeStatus.Failed, work.Command.SessionId, ex.Message));
        }
        finally
        {
            lock (this._stateLock)
            {
                var primaryCount = this._primaryCounts[primary] - 1;
                if (primaryCount == 0)
                {
                    this._primaryCounts.Remove(primary);
                }
                else
                {
                    this._primaryCounts[primary] = primaryCount;
                }

                foreach (var binding in bindings)
                {
                    if (this._tails.GetValueOrDefault(binding) == finished.Task)
                    {
                        this._tails.Remove(binding);
                    }
                }

                this._outstandingCount--;
                finished.TrySetResult();
                if (this._completed && this._outstandingCount == 0)
                {
                    this._drained.TrySetResult();
                }
            }
        }
    }
}