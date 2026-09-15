using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting.Tests;

internal sealed class SyntheticBackend(string behavior = "synthetic", HostedBackendContext? context = null)
    : IMediaSourcePolicyBackend
{
    private readonly Lock _gate = new();

    private readonly Channel<MediaBackendSignal> _signals
        = Channel.CreateBounded<MediaBackendSignal>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private int _artworkReads;
    private long _artworkVersion = 1;
    private int _commands;
    private int _failedReadsRemaining = behavior == "startup-read-failure" ? 2 : 0;
    private long _generation = 1;
    private int _invalidations;
    private MediaPlaybackState _playback = MediaPlaybackState.Paused;
    private MediaBackendSourcePolicy _policy = MediaBackendSourcePolicy.Empty;
    private long _revision;
    private string _title = "Initial";

    public TaskCompletionSource DisposalEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task? DisposalBarrier { get; init; }
    public bool Disposed { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return behavior switch
        {
            "hang-start" => new TaskCompletionSource().Task,
            "slow-start" => Task.Delay(800, cancellationToken),
            _ => Task.CompletedTask
        };
    }

    public async IAsyncEnumerable<MediaBackendSignal> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var signal in this._signals.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return signal;
        }
    }

    public Task<MediaBackendSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this._gate)
        {
            if (behavior == "hang-snapshot" && this._invalidations > 0)
            {
                return new TaskCompletionSource<MediaBackendSnapshot>().Task;
            }

            if (behavior == "recurring-read-failure" && this._revision > 0)
            {
                throw new IOException("Persistent snapshot failure.");
            }

            if (this._failedReadsRemaining > 0)
            {
                this._failedReadsRemaining--;
                throw new IOException("Injected transient snapshot failure.");
            }

            var excluded = this._policy.ExcludedApplicationIds.Contains("spike.player");
            if (behavior == "recurring-read-failure") { this.Signal(); }

            var source = new MediaSourceSnapshot("Synthetic player")
            {
                NativeApplication = new MediaNativeApplicationIdentity("spike.player")
            };
            var properties = MediaPropertiesSnapshot.Empty(source) with
            {
                Title = this._title,
                Subtitle
                = $"Invalidations: {this._invalidations}; Artwork reads: {this._artworkReads}; Commands: {this._commands};",
                Artwork = new MediaArtworkKey(new MediaSessionId(1), this._artworkVersion)
            };
            var session = new MediaBackendSessionSnapshot(new MediaBackendSessionId(1), this._generation, properties,
                MediaTimelinePropertiesSnapshot.Empty, this._playback,
                MediaCapabilities.Play | MediaCapabilities.Pause | MediaCapabilities.Stop | MediaCapabilities.SkipNext |
                MediaCapabilities.SkipPrevious |
                (context?.CanActivateSource == true ? MediaCapabilities.ActivateSource : MediaCapabilities.None));
            return Task.FromResult(
                new MediaBackendSnapshot(++this._revision, excluded ? [] : [session],
                    excluded ? [] : [new MediaBackendSessionId(1)], MediaControlAvailability.Available)
                {
                    SourcePolicyRevision = this._policy.Revision, Connection = MediaBackendConnectionState.Connected
                });
        }
    }

    public async Task ApplySourcePolicyAsync(MediaBackendSourcePolicy policy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (behavior == "slow-policy" && policy.Revision > 0)
        {
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }

        lock (this._gate)
        {
            if (policy.Revision < this._policy.Revision)
            {
                throw new ArgumentException("Policy revision decreased.", nameof(policy));
            }

            this._policy = policy;
        }

        this.Signal();
    }

    public void InvalidateObservations(ImmutableArray<MediaBackendObservationRequest> requests)
    {
        if (behavior == "invalidation-failure") { throw new IOException("Injected invalidation failure."); }

        lock (this._gate)
        {
            this._invalidations += requests.Count(static request => request.SessionId.Value == 1);
        }

        this.Signal();
    }

    public Task<MediaBackendCommandResult> ExecuteAsync(
        MediaBackendCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this._gate)
        {
            this._commands++;
            if (command.SessionId.Value != 1 || command.BindingGeneration != this._generation ||
                this._policy.ExcludedApplicationIds.Contains("spike.player"))
            {
                return Task.FromResult(new MediaBackendCommandResult(MediaBackendCommandStatus.SessionGone, null));
            }

            switch (command.Operation)
            {
                case MediaOperation.Play: this._playback = MediaPlaybackState.Playing; break;
                case MediaOperation.Pause: this._playback = MediaPlaybackState.Paused; break;
                case MediaOperation.SkipNext:
                    this._title = "Next";
                    this._artworkVersion++;
                    break;
                case MediaOperation.SkipPrevious:
                    this._generation++;
                    this._artworkVersion++;
                    break;
                case MediaOperation.Stop:
                    this._playback = MediaPlaybackState.Stopped;
                    if (behavior == "transient-read-failure") { this._failedReadsRemaining = 4; }

                    if (behavior == "persistent-read-failure") { this._failedReadsRemaining = int.MaxValue; }

                    if (behavior == "short-read-failure") { this._failedReadsRemaining = 1; }

                    break;
                case MediaOperation.ActivateSource: return this.ActivateAsync(cancellationToken);
                default:
                    return Task.FromResult(new MediaBackendCommandResult(MediaBackendCommandStatus.Unsupported, null));
            }
        }

        this.Signal();
        if (command.Operation == MediaOperation.SkipNext && behavior == "hang-command")
        {
            return new TaskCompletionSource<MediaBackendCommandResult>().Task;
        }

        if (command.Operation == MediaOperation.Stop && behavior == "cancel-command")
        {
            return WaitForCancellationAsync(cancellationToken);
        }

        if (command.Operation == MediaOperation.Stop && behavior is "slow-command" or "slow-cancel")
        {
            return this.SlowCommandAsync(cancellationToken);
        }

        return Task.FromResult(new MediaBackendCommandResult(MediaBackendCommandStatus.Completed, null));
    }

    public async ValueTask<MediaArtworkContent?> GetArtworkAsync(
        MediaArtworkKey key,
        CancellationToken cancellationToken)
    {
        long version;
        lock (this._gate)
        {
            version = this._artworkVersion;
            this._artworkReads++;
        }

        if (behavior != "quiet-artwork") { this.Signal(); }

        if (behavior == "slow-artwork")
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        if (behavior is "large-artwork" or "oversized-artwork")
        {
            var data = new byte[PipeProtocol.MaximumArtworkBytes + (behavior == "oversized-artwork" ? 1 : 0)];
            for (var index = 0; index < data.Length; index++)
            {
                data[index] = (byte)(index % 251);
            }

            return new MediaArtworkContent("application/octet-stream", data, null);
        }

        return key.SessionId.Value == 1 && key.Version == version
            ? new MediaArtworkContent("application/octet-stream", new byte[] { 1, 2, 3, (byte)version }, null)
            : null;
    }

    public async ValueTask DisposeAsync()
    {
        this.DisposalEntered.TrySetResult();
        if (this.DisposalBarrier is { } barrier)
        {
            await barrier.ConfigureAwait(false);
        }

        if (behavior == "fail-cleanup")
        {
            throw new InvalidOperationException("Synthetic cleanup failure.");
        }

        if (behavior == "hang-cleanup")
        {
            await new TaskCompletionSource().Task.ConfigureAwait(false);
        }

        this.Disposed = true;
        this._signals.Writer.TryComplete();
    }

    private void Signal() => this._signals.Writer.TryWrite(MediaBackendSignal.ObservationsChanged);

    private async Task<MediaBackendCommandResult> ActivateAsync(CancellationToken cancellationToken)
    {
        if (context?.CanActivateSource != true)
        {
            return new MediaBackendCommandResult(MediaBackendCommandStatus.Unsupported, null);
        }

        if (behavior == "delayed-activation")
        {
            this._title = "Activating";
            this.Signal();
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }

        var activated = await context
            .TryActivateSourceAsync(behavior == "invalid-activation" ? "other.player" : "spike.player", "Initial",
                cancellationToken)
            .ConfigureAwait(false);
        if (behavior == "duplicate-activation")
        {
            await context.TryActivateSourceAsync("spike.player", "Initial", cancellationToken).ConfigureAwait(false);
        }

        return new MediaBackendCommandResult(
            activated ? MediaBackendCommandStatus.Completed : MediaBackendCommandStatus.Failed, null);
    }

    private static async Task<MediaBackendCommandResult> WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return new MediaBackendCommandResult(MediaBackendCommandStatus.Completed, null);
    }

    private async Task<MediaBackendCommandResult> SlowCommandAsync(CancellationToken cancellationToken)
    {
        if (behavior == "slow-command")
        {
            await Task.Delay(5500, cancellationToken).ConfigureAwait(false);
            return new MediaBackendCommandResult(MediaBackendCommandStatus.Completed, null);
        }

        try { return await WaitForCancellationAsync(cancellationToken).ConfigureAwait(false); }
        finally { await Task.Delay(400, CancellationToken.None).ConfigureAwait(false); }
    }
}