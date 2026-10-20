using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting.Tests;

internal static class StopCompletionTests
{
    internal const string BackendPrefix = "stop-completion-";

    internal static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Stop completion accepts session removal before the result", () => RunAsync(StopScenario.Removed)),
        ("Stop completion accepts the result before session removal", () => RunAsync(StopScenario.ResultFirst)),
        ("Stop completion rejects a failed command after removal", () => RunAsync(StopScenario.Failed)),
        ("Stop completion exception does not apply to Pause", () => RunAsync(StopScenario.Pause)),
        ("Stop completion rejects an in-place replacement binding", () => RunAsync(StopScenario.Rebound)),
        ("Stop completion rejects a session that reappeared", () => RunAsync(StopScenario.Reappeared)),
        ("Stop completion rejects a changed source policy", () => RunAsync(StopScenario.PolicyChanged)),
        ("Stop completion rejects source exclusion", () => RunAsync(StopScenario.PolicyExcluded)),
        ("Stop completion rejects a disconnected source", () => RunAsync(StopScenario.Disconnected)),
        ("Stop completion rejects an unavailable source", () => RunAsync(StopScenario.Unavailable)),
        ("Stop completion rejects an observation failure", () => RunAsync(StopScenario.ObservationFailed)),
        ("Stop completion rejects a replaced worker", () => RunAsync(StopScenario.WorkerRestart)),
    ];

    private static async Task RunAsync(StopScenario scenario)
    {
        var id = $"{BackendPrefix}{(int)scenario}-{Guid.NewGuid():N}";
        using var entered = CreateEvent(id, "entered");
        using var advance = CreateEvent(id, "advance");
        using var release = CreateEvent(id, "release");
        await using var backend = new OutOfProcessMediaBackend(Program.Options(id));
        await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
        var initial = await HostingTests.WaitForSnapshotAsync(backend).ConfigureAwait(false);
        HostingTests.Check(initial.Sessions.Length == 1, "The initial session did not arrive.");
        var operation = scenario == StopScenario.Pause ? MediaOperation.Pause : MediaOperation.Stop;
        var pending = backend.ExecuteAsync(HostingTests.Command(initial, operation), CancellationToken.None);
        try
        {
            await HostingTests.EventuallyAsync(() => entered.WaitOne(0)).ConfigureAwait(false);
            if (scenario == StopScenario.Rebound)
            {
                await HostingTests.EventuallyAsync(async () =>
                    (await backend.ReadSnapshotAsync(default).ConfigureAwait(false)).Sessions.Single().BindingGeneration >
                    initial.Sessions[0].BindingGeneration).ConfigureAwait(false);
            }
            else if (scenario != StopScenario.ResultFirst)
            {
                await HostingTests.EventuallyAsync(async () =>
                    (await backend.ReadSnapshotAsync(default).ConfigureAwait(false)).Sessions.IsEmpty).ConfigureAwait(false);
            }

            if (scenario is StopScenario.PolicyChanged or StopScenario.PolicyExcluded)
            {
                await backend.ApplySourcePolicyAsync(new MediaBackendSourcePolicy(1,
                        [scenario == StopScenario.PolicyExcluded ? "stop.player" : "another.player"]), default)
                    .ConfigureAwait(false);
            }
            else if (scenario is StopScenario.Reappeared or StopScenario.Disconnected or
                     StopScenario.Unavailable or StopScenario.ObservationFailed)
            {
                advance.Set();
                await HostingTests.EventuallyAsync(async () =>
                {
                    try
                    {
                        var snapshot = await backend.ReadSnapshotAsync(default).ConfigureAwait(false);
                        return scenario switch
                        {
                            StopScenario.Reappeared => snapshot.Sessions.Length == 1 &&
                                snapshot.Sessions[0].Id != initial.Sessions[0].Id,
                            StopScenario.Disconnected => snapshot.Connection.Status == MediaConnectionStatus.Disconnected,
                            StopScenario.Unavailable => snapshot.Availability == MediaControlAvailability.Unavailable,
                            _ => false,
                        };
                    }
                    catch (IOException) when (scenario == StopScenario.ObservationFailed)
                    {
                        return true;
                    }
                }).ConfigureAwait(false);
            }
            else if (scenario == StopScenario.WorkerRestart)
            {
                var originalWorker = backend.WorkerProcessId;
                using var process = Process.GetProcessById(originalWorker);
                process.Kill();
                await process.WaitForExitAsync().ConfigureAwait(false);
                await HostingTests.EventuallyAsync(async () =>
                    backend.WorkerProcessId != 0 && backend.WorkerProcessId != originalWorker &&
                    (await backend.ReadSnapshotAsync(default).ConfigureAwait(false)).Sessions.Length == 1).ConfigureAwait(false);
            }

            release.Set();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var expected = scenario is StopScenario.Removed or StopScenario.ResultFirst
                ? MediaBackendCommandStatus.Completed
                : scenario == StopScenario.WorkerRestart ? MediaBackendCommandStatus.Unavailable : MediaBackendCommandStatus.SessionGone;
            HostingTests.Check(result.Status == expected, $"Expected {expected}, received {result.Status}: {result.DiagnosticMessage}");
            if (scenario == StopScenario.ResultFirst)
            {
                backend.InvalidateObservations([new(initial.Sessions[0].Id, MediaBackendObservationChanges.Playback)]);
                await HostingTests.EventuallyAsync(async () =>
                    (await backend.ReadSnapshotAsync(default).ConfigureAwait(false)).Sessions.IsEmpty).ConfigureAwait(false);
            }

            var oldPlay = await backend.ExecuteAsync(HostingTests.Command(initial, MediaOperation.Play), default).ConfigureAwait(false);
            HostingTests.Check(oldPlay.Status == MediaBackendCommandStatus.SessionGone, "Play reused a removed or replaced binding.");
        }
        finally
        {
            advance.Set();
            release.Set();
        }
    }

    internal static string EventName(string id, string phase) => $"Local\\MediaControls.{id}.{phase}";

    private static EventWaitHandle CreateEvent(string id, string phase) => new(false, EventResetMode.ManualReset, EventName(id, phase));
}

internal enum StopScenario
{
    Removed,
    ResultFirst,
    Failed,
    Pause,
    Rebound,
    Reappeared,
    PolicyChanged,
    PolicyExcluded,
    Disconnected,
    Unavailable,
    ObservationFailed,
    WorkerRestart,
}

internal sealed class StopCompletionBackend(string id) : IMediaSourcePolicyBackend
{
    private readonly Lock _gate = new();
    private readonly Channel<MediaBackendSignal> _signals = Channel.CreateUnbounded<MediaBackendSignal>();
    private readonly StopScenario _scenario = (StopScenario)int.Parse(
        id[StopCompletionTests.BackendPrefix.Length..].Split('-')[0], CultureInfo.InvariantCulture);
    private MediaBackendSourcePolicy _policy = MediaBackendSourcePolicy.Empty;
    private bool _visible = true;
    private bool _connected = true;
    private bool _available = true;
    private bool _readFails;
    private long _generation = 1;
    private long _revision;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async IAsyncEnumerable<MediaBackendSignal> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var signal in this._signals.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return signal;
        }
    }

    public Task<MediaBackendSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        lock (this._gate)
        {
            if (this._readFails) { throw new IOException("Controlled observation failure."); }

            var source = new MediaSourceSnapshot("Stop player") { NativeApplication = new("stop.player") };
            var session = new MediaBackendSessionSnapshot(new(1), this._generation, MediaPropertiesSnapshot.Empty(source),
                MediaTimelinePropertiesSnapshot.Empty, MediaPlaybackState.Playing,
                MediaCapabilities.Play | MediaCapabilities.Pause | MediaCapabilities.Stop);
            var visible = this._visible && !this._policy.ExcludedApplicationIds.Contains("stop.player");
            return Task.FromResult(new MediaBackendSnapshot(++this._revision, visible ? [session] : [], [],
                this._available ? MediaControlAvailability.Available : MediaControlAvailability.Unavailable)
            {
                SourcePolicyRevision = this._policy.Revision,
                Connection = this._connected ? MediaBackendConnectionState.Connected : MediaBackendConnectionState.Disconnected,
            });
        }
    }

    public Task ApplySourcePolicyAsync(MediaBackendSourcePolicy policy, CancellationToken cancellationToken)
    {
        lock (this._gate) { this._policy = policy; }

        this.Signal();
        return Task.CompletedTask;
    }

    public void InvalidateObservations(ImmutableArray<MediaBackendObservationRequest> requests)
    {
        if (this._scenario == StopScenario.ResultFirst)
        {
            lock (this._gate) { this._visible = false; }

            this.Signal();
        }
    }

    public async Task<MediaBackendCommandResult> ExecuteAsync(MediaBackendCommand command, CancellationToken cancellationToken)
    {
        lock (this._gate)
        {
            this._visible = this._scenario is StopScenario.ResultFirst or StopScenario.Rebound;
            if (this._scenario == StopScenario.Rebound) { this._generation++; }
        }

        this.Signal();
        using var entered = EventWaitHandle.OpenExisting(StopCompletionTests.EventName(id, "entered"));
        entered.Set();
        if (this._scenario is StopScenario.Reappeared or StopScenario.Disconnected or
            StopScenario.Unavailable or StopScenario.ObservationFailed)
        {
            await this.WaitForEventAsync("advance", cancellationToken).ConfigureAwait(false);
            lock (this._gate)
            {
                this._visible = this._scenario == StopScenario.Reappeared;
                if (this._visible) { this._generation++; }

                this._connected = this._scenario != StopScenario.Disconnected;
                this._available = this._scenario != StopScenario.Unavailable;
                this._readFails = this._scenario == StopScenario.ObservationFailed;
            }

            this.Signal();
        }

        await this.WaitForEventAsync("release", cancellationToken).ConfigureAwait(false);
        return new(this._scenario == StopScenario.Failed ? MediaBackendCommandStatus.Failed : MediaBackendCommandStatus.Completed, null);
    }

    public ValueTask<MediaArtworkContent?> GetArtworkAsync(MediaArtworkKey key, CancellationToken cancellationToken) =>
        new((MediaArtworkContent?)null);

    public ValueTask DisposeAsync()
    {
        this._signals.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private void Signal() => this._signals.Writer.TryWrite(MediaBackendSignal.SessionsChanged | MediaBackendSignal.ObservationsChanged);

    private async Task WaitForEventAsync(string phase, CancellationToken cancellationToken)
    {
        using var signal = EventWaitHandle.OpenExisting(StopCompletionTests.EventName(id, phase));
        await HostingTests.EventuallyAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return signal.WaitOne(0);
        }).ConfigureAwait(false);
    }
}