// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media;

/// <summary>Orders commands sharing session bindings while allowing independent commands to progress.</summary>
internal sealed class MediaCommandScheduler
{
    internal const int MaximumOutstandingCommands = 64;
    private const int MaximumCommandsPerBinding = 2;

    private readonly Lock _stateLock = new();
    private readonly List<Entry> _pending = [];
    private readonly HashSet<Entry> _active = [];
    private readonly HashSet<MediaBackendSessionTarget> _activeBindings = [];
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<MediaService.CommandWork, CancellationToken, Task> _execute;
    private readonly CancellationToken _cancellationToken;
    private readonly CancellationTokenRegistration _cancellation;

    private bool _completed;

    public MediaCommandScheduler(
        Func<MediaService.CommandWork, CancellationToken, Task> execute,
        CancellationToken cancellationToken)
    {
        this._execute = execute;
        this._cancellationToken = cancellationToken;
        this._cancellation = cancellationToken.Register(this.CancelAll);
    }

    public bool TrySchedule(MediaService.CommandWork work)
    {
        var command = work.Command;
        var primary = new MediaBackendSessionTarget(command.BackendSessionId, command.BindingGeneration);
        lock (this._stateLock)
        {
            if (this._completed || this._cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            var preceding = this._pending.LastOrDefault(entry => entry.Primary == primary);
            var replaced = IsPlayback(work) && preceding is not null && IsPlayback(preceding.Work) ? preceding : null;
            if (replaced is null &&
                (this._pending.Count + this._active.Count >= MaximumOutstandingCommands ||
                 this._pending.Count(entry => entry.Primary == primary) +
                 this._active.Count(entry => entry.Primary == primary) >= MaximumCommandsPerBinding))
            {
                return false;
            }

            if (replaced is not null)
            {
                this._pending.Remove(replaced);
                replaced.Work.Complete(new(replaced.Work.OperationId, MediaCommandOutcomeStatus.Superseded,
                    replaced.Work.Command.SessionId, "A newer playback request replaced this command."));
            }

            this._pending.Add(new(work, primary, command.SessionsToPause.Prepend(primary).Distinct().ToArray()));
            this.StartReadyCommandsUnderLock();
            return true;
        }
    }

    /// <summary>Captures the bindings of queued or executing Play and Pause commands.</summary>
    public HashSet<MediaBackendSessionTarget> GetOutstandingPlaybackTargets()
    {
        lock (this._stateLock)
        {
            return this._pending.Concat(this._active)
                .Where(static entry => IsPlayback(entry.Work))
                .Select(static entry => entry.Primary)
                .ToHashSet();
        }
    }

    /// <summary>Removes deferred playback touching any unconfirmed binding.</summary>
    public MediaService.CommandWork[] RemovePendingPlayback(HashSet<MediaBackendSessionTarget> targets)
    {
        lock (this._stateLock)
        {
            var removed = this._pending.Where(entry => IsPlayback(entry.Work) && entry.Bindings.Any(targets.Contains))
                .ToArray();
            foreach (var entry in removed)
            {
                this._pending.Remove(entry);
            }

            this.StartReadyCommandsUnderLock();
            return [.. removed.Select(static entry => entry.Work)];
        }
    }

    /// <summary>Stops admission and drains accepted work; the owner cancels the lifetime token.</summary>
    public Task CompleteAsync()
    {
        lock (this._stateLock)
        {
            this._completed = true;
            this.CompleteIfDrainedUnderLock();
            return this._drained.Task;
        }
    }

    private static bool IsPlayback(MediaService.CommandWork work)
    {
        return work.Command.ResolvedOperation is MediaOperation.Play or MediaOperation.Pause;
    }

    private void StartReadyCommandsUnderLock()
    {
        HashSet<MediaBackendSessionTarget> waitingBindings = [];
        for (var index = 0; index < this._pending.Count;)
        {
            var entry = this._pending[index];
            if (entry.Bindings.Any(binding => this._activeBindings.Contains(binding) || waitingBindings.Contains(binding)))
            {
                waitingBindings.UnionWith(entry.Bindings);
                index++;
                continue;
            }

            this._pending.RemoveAt(index);
            this._active.Add(entry);
            this._activeBindings.UnionWith(entry.Bindings);
            _ = Task.Run(() => this.RunAsync(entry));
        }
    }

    private void CancelAll()
    {
        lock (this._stateLock)
        {
            foreach (var entry in this._pending.Concat(this._active))
            {
                entry.Work.Cancel();
            }

            this._pending.Clear();
            this.CompleteIfDrainedUnderLock();
        }
    }

    private void CompleteIfDrainedUnderLock()
    {
        if (this._completed && this._pending.Count == 0 && this._active.Count == 0)
        {
            this._cancellation.Unregister();
            this._drained.TrySetResult();
        }
    }

    private async Task RunAsync(Entry entry)
    {
        var work = entry.Work;
        try
        {
            await work.WaitUntilReadyAsync(this._cancellationToken).ConfigureAwait(false);
            this._cancellationToken.ThrowIfCancellationRequested();
            await this._execute(work, this._cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (this._cancellationToken.IsCancellationRequested)
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
                this._active.Remove(entry);
                this._activeBindings.ExceptWith(entry.Bindings);
                this.StartReadyCommandsUnderLock();
                this.CompleteIfDrainedUnderLock();
            }
        }
    }

    private sealed record Entry(
        MediaService.CommandWork Work,
        MediaBackendSessionTarget Primary,
        MediaBackendSessionTarget[] Bindings);
}