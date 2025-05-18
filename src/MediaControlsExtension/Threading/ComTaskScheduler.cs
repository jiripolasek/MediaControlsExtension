// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Collections.Concurrent;

namespace JPSoftworks.MediaControlsExtension.Threading;

internal sealed partial class ComTaskScheduler : TaskScheduler, IDisposable
{
    private readonly CancellationTokenSource _cancellationToken;
    private readonly BlockingCollection<Task> _tasks;
    private readonly List<Thread> _threads;
    public readonly List<int> ThreadIds;

    public override int MaximumConcurrencyLevel => this._threads.Count;

    public ComTaskScheduler(int numberOfThreads)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(numberOfThreads, 1);

        // Initialize the tasks collection
        this._tasks = [];
        this._cancellationToken = new CancellationTokenSource();

        // Create the threads to be used by this scheduler
        this._threads =
        [
            .. Enumerable.Range(0, numberOfThreads).Select(_ =>
            {
                var thread = new Thread(this.ThreadStart) { IsBackground = true };
                thread.SetApartmentState(ApartmentState.STA);
                return thread;
            })
        ];

        // Start all of the threads
        this._threads.ForEach(static t => t.Start());

        this.ThreadIds = [.. this._threads.Select(static x => x.ManagedThreadId)];
    }

    public void Dispose()
    {
        if (this._cancellationToken.IsCancellationRequested)
        {
            return;
        }

        this._cancellationToken.Cancel();
        this._cancellationToken.Dispose();
        this._tasks.CompleteAdding();
        this._tasks.Dispose();
    }

    protected override void QueueTask(Task task)
    {
        this.ThrowIfDisposed();

        this._tasks.Add(task, this._cancellationToken.Token);
    }

    protected override IEnumerable<Task> GetScheduledTasks()
    {
        this.ThrowIfDisposed();

        return this._tasks.ToArray();
    }

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
    {
        this.ThrowIfDisposed();

        if (!this.ThreadIds.Contains(Environment.CurrentManagedThreadId))
        {
            return false;
        }

        if (this._cancellationToken.Token.IsCancellationRequested)
        {
            return false;
        }

        return this.TryExecuteTask(task);
    }

    private void ThreadStart()
    {
        var token = this._cancellationToken.Token;

        foreach (var task in this._tasks.GetConsumingEnumerable(token))
        {
            this.TryExecuteTask(task);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            this._cancellationToken.IsCancellationRequested || this._tasks.IsAddingCompleted, typeof(ComTaskScheduler));
    }
}