// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;

internal sealed class FakeSourcePolicyBackend(MediaBackendSnapshot snapshot) : IMediaSourcePolicyBackend
{
    private readonly Lock _stateLock = new();
    private readonly TaskCompletionSource _releasePolicy = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseCommand = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, long> _sourceEpochs = new(StringComparer.Ordinal);
    private MediaBackendSourcePolicy _policy = MediaBackendSourcePolicy.Empty;
    private int _blockNextRead;

    public FakeMediaBackend Inner { get; } = new(snapshot);
    public TaskCompletionSource PolicyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReadCaptured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CommandQueued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool BlockPolicy { get; set; }
    public bool BlockCommands { get; set; }
    public bool FailPolicy { get; set; }
    public bool ReturnOldPolicyRevision { get; set; }
    public long PolicyRevisionAtStart { get; private set; }

    public MediaBackendSourcePolicy Policy
    {
        get
        {
            lock (this._stateLock)
            {
                return this._policy;
            }
        }
    }

    public async Task ApplySourcePolicyAsync(MediaBackendSourcePolicy policy, CancellationToken cancellationToken)
    {
        lock (this._stateLock)
        {
            foreach (var applicationId in policy.ExcludedApplicationIds.Except(this._policy.ExcludedApplicationIds))
            {
                this._sourceEpochs[applicationId] = this._sourceEpochs.GetValueOrDefault(applicationId) + 1;
            }

            this._policy = policy;
        }

        if (policy.Revision > 0)
        {
            this.PolicyStarted.TrySetResult();
            if (this.BlockPolicy)
            {
                await this._releasePolicy.Task.WaitAsync(cancellationToken);
            }

            if (this.FailPolicy)
            {
                throw new InvalidOperationException("Injected policy failure.");
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        this.PolicyRevisionAtStart = this.Policy.Revision;
        return this.Inner.StartAsync(cancellationToken);
    }

    public IAsyncEnumerable<MediaBackendSignal> WatchAsync(CancellationToken cancellationToken) => this.Inner.WatchAsync(cancellationToken);

    public async Task<MediaBackendSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        var current = await this.Inner.ReadSnapshotAsync(cancellationToken);
        var policy = this.Policy;
        var filtered = current with
        {
            Sessions = [.. current.Sessions.Where(session => session.MediaProperties.Source.NativeApplication is not { } application || !policy.ExcludedApplicationIds.Contains(application.ApplicationId))],
            SourcePolicyRevision = this.ReturnOldPolicyRevision ? 0 : policy.Revision,
        };
        if (Interlocked.Exchange(ref this._blockNextRead, 0) != 0)
        {
            this.ReadCaptured.TrySetResult();
            await this._releaseRead.Task.WaitAsync(cancellationToken);
        }

        return filtered;
    }

    public async Task<MediaBackendCommandResult> ExecuteAsync(MediaBackendCommand command, CancellationToken cancellationToken)
    {
        var current = await this.Inner.ReadSnapshotAsync(cancellationToken);
        var target = current.Sessions.Single(session => session.Id == command.SessionId);
        var applicationId = target.MediaProperties.Source.NativeApplication?.ApplicationId ?? string.Empty;
        long epoch;
        lock (this._stateLock)
        {
            if (this._policy.ExcludedApplicationIds.Contains(applicationId))
            {
                return new(MediaBackendCommandStatus.SessionGone, null);
            }

            epoch = this._sourceEpochs.GetValueOrDefault(applicationId);
        }

        if (this.BlockCommands)
        {
            this.CommandQueued.TrySetResult();
            await this._releaseCommand.Task.WaitAsync(cancellationToken);
        }

        lock (this._stateLock)
        {
            if (epoch != this._sourceEpochs.GetValueOrDefault(applicationId))
            {
                return new(MediaBackendCommandStatus.SessionGone, null);
            }
        }

        return await this.Inner.ExecuteAsync(command, cancellationToken);
    }

    public void InvalidateObservations(ImmutableArray<MediaBackendObservationRequest> requests) => this.Inner.InvalidateObservations(requests);

    public ValueTask<MediaArtworkContent?> GetArtworkAsync(MediaArtworkKey key, CancellationToken cancellationToken) =>
        this.Inner.GetArtworkAsync(key, cancellationToken);

    public ValueTask DisposeAsync() => this.Inner.DisposeAsync();
    public void BlockNextRead() => Volatile.Write(ref this._blockNextRead, 1);
    public void ReleasePolicy() => this._releasePolicy.TrySetResult();
    public void ReleaseRead() => this._releaseRead.TrySetResult();
    public void ReleaseCommands() => this._releaseCommand.TrySetResult();
}