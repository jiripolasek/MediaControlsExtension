namespace JPSoftworks.MediaControlsExtension.Media.Hosting;

/// <summary>Owns worker lifetimes for one extension instance, including shutdown before service cleanup finishes.</summary>
public sealed class MediaWorkerOwner : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly HashSet<OutOfProcessMediaBackend> _backends = [];
    private readonly HashSet<OwnedWorkerProcess> _processes = [];
    private readonly Func<string, string[], OwnedWorkerProcess> _startWorker;
    private Task? _shutdown;

    /// <summary>Creates an owner with no workers; disposal permanently revokes process creation.</summary>
    public MediaWorkerOwner() : this(static (executable, arguments) => OwnedWorkerProcess.Start(executable, arguments)) { }

    internal MediaWorkerOwner(Func<string, string[], OwnedWorkerProcess> startWorker) => this._startWorker = startWorker;

    internal void Attach(OutOfProcessMediaBackend backend)
    {
        lock (this._gate)
        {
            ObjectDisposedException.ThrowIf(this._shutdown is not null, this);
            this._backends.Add(backend);
        }
    }

    internal void Detach(OutOfProcessMediaBackend backend)
    {
        lock (this._gate) { this._backends.Remove(backend); }
    }

    internal OwnedWorkerProcess StartWorker(string executable, string[] arguments)
    {
        lock (this._gate)
        {
            ObjectDisposedException.ThrowIf(this._shutdown is not null, this);
        }

        var process = this._startWorker(executable, arguments);
        lock (this._gate)
        {
            if (this._shutdown is null)
            {
                this._processes.Add(process);
                return process;
            }
        }

        process.Dispose();
        throw new ObjectDisposedException(nameof(MediaWorkerOwner));
    }

    internal void ReleaseWorker(OwnedWorkerProcess process)
    {
        lock (this._gate) { this._processes.Remove(process); }
        process.Dispose();
    }

    /// <summary>Immediately revokes new workers and begins bounded shutdown without waiting for unrelated cleanup.</summary>
    public void RequestStop()
    {
        lock (this._gate)
        {
            if (this._shutdown is not null)
            {
                return;
            }

            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            this._shutdown = stopped.Task;
            var pending = this._backends.Select(static backend => backend.DisposeAsync().AsTask()).ToArray();
            _ = this.ShutdownAsync(pending, stopped);
        }
    }

    /// <summary>Stops this owner and waits for its workers to exit.</summary>
    public ValueTask DisposeAsync()
    {
        this.RequestStop();
        lock (this._gate) { return new(this._shutdown!); }
    }

    private async Task ShutdownAsync(Task[] pending, TaskCompletionSource stopped)
    {
        try
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The independent job deadline covers a stalled proxy shutdown.
            }
            finally
            {
                lock (this._gate)
                {
                    foreach (var process in this._processes)
                    {
                        process.CloseJob();
                    }
                }
            }

            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            stopped.TrySetResult();
        }
        catch (Exception ex)
        {
            // Closing every job enforces lifetime even when graceful shutdown fails.
            stopped.TrySetException(ex);
        }
    }
}