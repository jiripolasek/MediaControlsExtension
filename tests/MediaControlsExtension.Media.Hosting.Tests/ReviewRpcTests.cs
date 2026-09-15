using System.Buffers.Binary;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting.Tests;

internal static class ReviewRpcTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("RPC partial write failure retires the owner connection without escaping policy update", WriteFailureAsync),
        ("RPC artwork rejects negative lengths", () => ArtworkAsync("negative", false)),
        ("RPC artwork rejects oversized lengths", () => ArtworkAsync("oversized", false)),
        ("RPC artwork rejects missing headers", () => ArtworkAsync("missing", false)),
        ("RPC artwork rejects truncated payloads", () => ArtworkAsync("truncated", false)),
        ("RPC artwork rejects trailing bytes", () => ArtworkAsync("trailing", false)),
        ("RPC artwork rejects mismatched metadata", () => ArtworkAsync("mismatch", false)),
        ("RPC artwork accepts EOF after the reply and preserves control traffic",
            () => ArtworkAsync("delayed-eof", true)),
        ("RPC null snapshot retires its worker", () => NullMessageAsync("null-snapshot")),
        ("RPC null snapshot failure retires its worker", () => NullMessageAsync("null-snapshot-failure")),
        ("RPC null fault retires its worker", () => NullMessageAsync("null-fault")),
        ("RPC null activation request retires its worker", () => NullMessageAsync("null-activation")),
        ("RPC null nested activation retires its worker", () => NullMessageAsync("null-nested-activation")),
        ("RPC retirement preserves the first reported worker fault", () => NullMessageAsync("fault-before-activation")),
        ("RPC invalidation failure retains its disconnect reason", InvalidationFailureAsync),
        ("RPC invalidations survive a full request queue", () => InvalidationSaturationAsync(false)),
        ("RPC disposal releases invalidations waiting for request capacity", () => InvalidationSaturationAsync(true)),
        ("RPC completion timeout retires the policy connection without escaping", CompletionTimeoutAsync),
        ("RPC caller cancellation disarms the sent request deadline", CanceledCompletionAsync)
    ];

    private static async Task WriteFailureAsync()
    {
        var id = $"review-partial-request-{Guid.NewGuid():N}";
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, id);
        await using var backend = new OutOfProcessMediaBackend(Program.Options(id) with
        {
            MaximumRestarts = 0,
            RequestTimeout = TimeSpan.FromMilliseconds(500),
            ShutdownTimeout = TimeSpan.FromMilliseconds(500)
        });
        await backend.StartAsync(default).ConfigureAwait(false);
        await HostingTests.WaitForSnapshotAsync(backend).ConfigureAwait(false);
        try
        {
            await backend.ApplySourcePolicyAsync(new MediaBackendSourcePolicy(1, [new string('x', 2 * 1024 * 1024)]),
                    default)
                .WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await HostingTests.EventuallyAsync(() => backend.WorkerProcessId == 0).ConfigureAwait(false);
            await CheckFailureAsync(backend, "The worker frame write timed out.").ConfigureAwait(false);
        }
        finally { release.Set(); }
    }

    private static async Task ArtworkAsync(string mode, bool valid)
    {
        await using var backend = CreateBackend(mode);
        await backend.StartAsync(default).ConfigureAwait(false);
        var snapshot = await HostingTests.WaitForSnapshotAsync(backend).ConfigureAwait(false);
        var epoch = backend.WorkerEpoch;
        var content = await backend.GetArtworkAsync(snapshot.Sessions[0].MediaProperties.Artwork!.Value, default)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (valid)
        {
            HostingTests.Check(content?.Data.Length == 12,
                "The owner failed to drain a successful artwork transfer through EOF.");
            await CheckConnectionAsync(backend, snapshot, epoch).ConfigureAwait(false);
        }
        else
        {
            HostingTests.Check(content is null, "Malformed artwork escaped the transport boundary.");
            await HostingTests.EventuallyAsync(() => backend.WorkerProcessId == 0).ConfigureAwait(false);
            await CheckFailureAsync(backend, mode switch
            {
                "negative" or "oversized" => "Invalid artwork length.",
                "missing" or "truncated" => "The worker sent incomplete artwork.",
                "trailing" => "The worker sent trailing artwork bytes.",
                "mismatch" => "Artwork length does not match its metadata.",
                _ => throw new ArgumentException("Unknown malformed artwork mode.", nameof(mode))
            }).ConfigureAwait(false);
        }
    }

    private static async Task NullMessageAsync(string mode)
    {
        await using var backend = CreateBackend(mode);
        await backend.StartAsync(default).ConfigureAwait(false);
        var snapshot = await HostingTests.WaitForSnapshotAsync(backend).ConfigureAwait(false);
        await backend.ExecuteAsync(HostingTests.Command(snapshot, MediaOperation.Play), default).ConfigureAwait(false);
        await HostingTests.EventuallyAsync(() => backend.WorkerProcessId == 0).ConfigureAwait(false);
        await CheckFailureAsync(backend, mode switch
        {
            "fault-before-activation" => "A reported worker failure.",
            "null-activation" or "null-nested-activation" =>
                "The activation request is missing or belongs to a different worker lifetime.",
            _ => "The notice is missing or belongs to a different worker lifetime."
        }).ConfigureAwait(false);
    }

    private static async Task CompletionTimeoutAsync()
    {
        await using var backend = CreateBackend("delayed-policy");
        await backend.StartAsync(default).ConfigureAwait(false);
        await HostingTests.WaitForSnapshotAsync(backend).ConfigureAwait(false);
        await backend.ApplySourcePolicyAsync(new MediaBackendSourcePolicy(1, []), default).ConfigureAwait(false);
        await CheckFailureAsync(backend, "The worker request timed out; its outcome may be unknown.")
            .ConfigureAwait(false);
    }

    private static async Task InvalidationFailureAsync()
    {
        await using var backend
            = new OutOfProcessMediaBackend(Program.Options("invalidation-failure") with { MaximumRestarts = 0 });
        await backend.StartAsync(default).ConfigureAwait(false);
        var snapshot = await HostingTests.WaitForSnapshotAsync(backend).ConfigureAwait(false);
        backend.InvalidateObservations([
            new MediaBackendObservationRequest(snapshot.Sessions[0].Id, MediaBackendObservationChanges.Playback)
        ]);
        await CheckFailureAsync(backend, "Observation invalidation failed: Injected invalidation failure.")
            .ConfigureAwait(false);
    }

    private static async Task CheckFailureAsync(OutOfProcessMediaBackend backend, string reason)
    {
        string? diagnostic = null;
        await HostingTests.EventuallyAsync(async () =>
        {
            diagnostic = (await backend.ReadSnapshotAsync(default).ConfigureAwait(false)).Connection.DiagnosticMessage;
            return diagnostic?.StartsWith("Worker supervision stopped:", StringComparison.Ordinal) == true;
        }).ConfigureAwait(false);
        HostingTests.Check(diagnostic == "Worker supervision stopped: Worker restart budget exhausted: " + reason,
            $"The worker lost or replaced its disconnect reason: {diagnostic}");
    }

    private static async Task InvalidationSaturationAsync(bool dispose)
    {
        await using var backend = new OutOfProcessMediaBackend(Program.Options("review-admission") with
        {
            MaximumRestarts = 0,
            RequestTimeout = TimeSpan.FromSeconds(10),
            ShutdownTimeout = TimeSpan.FromMilliseconds(500)
        });
        await backend.StartAsync(default).ConfigureAwait(false);
        var snapshot = await HostingTests.WaitForSnapshotAsync(backend).ConfigureAwait(false);
        var epoch = backend.WorkerEpoch;
        using var cancel = new CancellationTokenSource();
        var admitted = Enumerable.Range(0, 32)
            .Select(_ => backend.ExecuteAsync(HostingTests.Command(snapshot, MediaOperation.Stop), cancel.Token))
            .ToArray();
        await HostingTests.EventuallyAsync(async () =>
            (await backend.ReadSnapshotAsync(default).ConfigureAwait(false)).Sessions[0].MediaProperties.Title ==
            "Admitted 32").ConfigureAwait(false);
        cancel.Cancel();
        try { await Task.WhenAll(admitted).ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        var queued = Enumerable.Range(0, 32)
            .Select(_ => backend.ExecuteAsync(HostingTests.Command(snapshot, MediaOperation.Play), default)).ToArray();
        backend.InvalidateObservations([
            new MediaBackendObservationRequest(snapshot.Sessions[0].Id, MediaBackendObservationChanges.Playback)
        ]);
        await Task.Delay(150).ConfigureAwait(false);
        HostingTests.Check(
            backend.WorkerProcessId != 0 && backend.WorkerEpoch == epoch && queued.All(task => !task.IsCompleted),
            "Invalidating a full request queue retired its worker or released pending requests.");
        for (var index = 0; index < 16; index++)
        {
            backend.InvalidateObservations([
                new MediaBackendObservationRequest(snapshot.Sessions[0].Id, MediaBackendObservationChanges.Timeline)
            ]);
        }

        if (dispose)
        {
            await backend.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await Task.WhenAll(queued).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        else
        {
            var results = await Task.WhenAll(queued).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            HostingTests.Check(results.All(result => result.Status == MediaBackendCommandStatus.Completed),
                "Queued commands did not complete after capacity returned.");
            await HostingTests.EventuallyAsync(async () =>
                (await backend.ReadSnapshotAsync(default).ConfigureAwait(false)).Sessions[0].MediaProperties.Title ==
                "Invalidated 3").ConfigureAwait(false);
            await CheckConnectionAsync(backend, snapshot, epoch).ConfigureAwait(false);
        }
    }

    private static async Task CanceledCompletionAsync()
    {
        await using var backend = CreateBackend("delayed-policy");
        await backend.StartAsync(default).ConfigureAwait(false);
        var snapshot = await HostingTests.WaitForSnapshotAsync(backend).ConfigureAwait(false);
        var epoch = backend.WorkerEpoch;
        using var caller = new CancellationTokenSource();
        var policy = backend.ApplySourcePolicyAsync(new MediaBackendSourcePolicy(1, []), caller.Token);
        await HostingTests.EventuallyAsync(async () =>
            (await backend.ReadSnapshotAsync(default).ConfigureAwait(false)).Sessions[0].MediaProperties.Title ==
            "Policy received").ConfigureAwait(false);
        caller.Cancel();
        try
        {
            await policy.ConfigureAwait(false);
            throw new InvalidOperationException("Cancellation did not release the caller.");
        }
        catch (OperationCanceledException) { }

        await Task.Delay(1000).ConfigureAwait(false);
        await CheckConnectionAsync(backend, snapshot, epoch).ConfigureAwait(false);
    }

    private static OutOfProcessMediaBackend CreateBackend(string mode) => new(Program.Options("review-rpc-" + mode) with
    {
        MaximumRestarts = 0,
        ObservationTimeout = TimeSpan.FromSeconds(3),
        PolicyTimeout = TimeSpan.FromMilliseconds(400)
    });

    private static async Task CheckConnectionAsync(
        OutOfProcessMediaBackend backend,
        MediaBackendSnapshot snapshot,
        Guid epoch)
    {
        var result = await backend.ExecuteAsync(HostingTests.Command(snapshot, MediaOperation.Pause), default)
            .ConfigureAwait(false);
        HostingTests.Check(
            result.Status == MediaBackendCommandStatus.Completed && backend.WorkerEpoch == epoch &&
            backend.WorkerProcessId != 0,
            "A successful or canceled transfer retired a usable connection.");
    }
}

internal sealed class ReviewRpcPeer(string mode) : TestWorkerRpc
{
    public override async Task<MediaBackendCommandResult> ExecuteAsync(
        WorkerCommand request,
        CancellationToken cancellationToken)
    {
        switch (mode)
        {
            case "null-snapshot":
                await this.Endpoint.Rpc.NotifyAsync(nameof(IOwnerNotifications.Snapshot), new object?[] { null })
                    .ConfigureAwait(false);
                break;
            case "null-snapshot-failure":
                await this.Endpoint.Rpc.NotifyAsync(nameof(IOwnerNotifications.SnapshotFailed), new object?[] { null })
                    .ConfigureAwait(false);
                break;
            case "null-fault":
                await this.Owner.ReportFaultAsync(null!, cancellationToken).ConfigureAwait(false);
                break;
            case "null-activation":
                await this.Owner.ActivateSourceAsync(null!, cancellationToken).ConfigureAwait(false);
                break;
            case "null-nested-activation":
                await this.Owner.ActivateSourceAsync(new WorkerActivation(this.Epoch, null!), cancellationToken)
                    .ConfigureAwait(false);
                break;
            case "fault-before-activation":
                await this.Owner
                    .ReportFaultAsync(new WorkerFault(this.Epoch, "A reported worker failure."), cancellationToken)
                    .ConfigureAwait(false);
                await this.Owner.ActivateSourceAsync(null!, cancellationToken).ConfigureAwait(false);
                break;
        }

        return new MediaBackendCommandResult(MediaBackendCommandStatus.Completed, null);
    }

    public override async Task<long> ApplyPolicyAsync(
        Guid epoch,
        SourcePolicyMessage policy,
        CancellationToken cancellationToken)
    {
        if (mode == "delayed-policy")
        {
            await this.PublishAsync("Policy received", policy.Revision).ConfigureAwait(false);
            await Task.Delay(800, CancellationToken.None).ConfigureAwait(false);
        }

        return policy.Revision;
    }

    public override async Task<WorkerArtwork?> CopyArtworkAsync(
        Guid epoch,
        MediaArtworkKey key,
        Stream destination,
        CancellationToken cancellationToken)
    {
        if (mode == "delayed-eof")
        {
            _ = WriteDelayedArtworkAsync(destination, cancellationToken);
            return new WorkerArtwork("image/png", 12, null);
        }

        await using (destination.ConfigureAwait(false))
        {
            if (mode == "missing") { return new WorkerArtwork("image/png", 12, null); }

            var header = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, mode switch
            {
                "negative" => -1, "oversized" => PipeProtocol.MaximumArtworkBytes + 1, _ => 12
            });
            await destination.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            if (mode is not ("negative" or "oversized"))
            {
                await destination.WriteAsync(new byte[mode switch { "truncated" => 10, "trailing" => 14, _ => 12 }],
                    cancellationToken).ConfigureAwait(false);
            }

            return new WorkerArtwork("image/png", mode == "mismatch" ? 10 : 12, null);
        }
    }

    private static async Task WriteDelayedArtworkAsync(Stream destination, CancellationToken cancellationToken)
    {
        await using (destination.ConfigureAwait(false))
        {
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            var header = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, 12);
            await destination.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await destination.WriteAsync(new byte[12], cancellationToken).ConfigureAwait(false);
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
    }
}