using System.IO.Pipes;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting.Tests;

internal static class ReviewAdmissionPeer
{
    public static async Task RunAsync(string pipeName, Guid owner, int processId)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        PipeProtocol.VerifyPeer(pipe, processId, server: false);
        using var protocol = new PipeProtocol(pipe);
        var hello = await protocol.ReadAsync(deadline.Token).ConfigureAwait(false);
        protocol.WriteTimeout = hello.RequestTimeout;
        var epoch = Guid.NewGuid();
        await protocol.WriteAsync(new(MessageKind.Welcome, owner, epoch), deadline.Token).ConfigureAwait(false);
        await using var backend = new SyntheticBackend();
        var snapshot = await backend.ReadSnapshotAsync(default).ConfigureAwait(false);
        await protocol.WriteAsync(new(MessageKind.Snapshot, owner, epoch) { Snapshot = snapshot }, deadline.Token).ConfigureAwait(false);
        var admitted = new List<long>();
        var canceled = new HashSet<long>();
        var release = Task.CompletedTask;
        try
        {
            while (true)
            {
                var request = await protocol.ReadAsync(deadline.Token).ConfigureAwait(false);
                if (request.Kind == MessageKind.Shutdown) { return; }
                if (request.Kind == MessageKind.Execute && request.Command?.Operation == MediaOperation.Stop)
                {
                    admitted.Add(request.RequestId);
                    snapshot = WithTitle(snapshot, $"Admitted {admitted.Count}");
                    await protocol.WriteAsync(new(MessageKind.Snapshot, owner, epoch) { Snapshot = snapshot }, deadline.Token).ConfigureAwait(false);
                }
                else if (request.Kind == MessageKind.Cancel && admitted.Contains(request.RequestId) && canceled.Add(request.RequestId) && canceled.Count == 32)
                {
                    release = ReleaseAsync(admitted.ToArray(), snapshot);
                }
                else if (request.Kind is MessageKind.Execute or MessageKind.Invalidate)
                {
                    await protocol.WriteAsync(new(MessageKind.Reply, owner, epoch, request.RequestId)
                    {
                        Result = new(MediaBackendCommandStatus.Completed, null),
                    }, deadline.Token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await deadline.CancelAsync().ConfigureAwait(false);
            try { await release.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        async Task ReleaseAsync(long[] ids, MediaBackendSnapshot current)
        {
            // Keep canceled work admitted beyond one new caller's admission budget.
            await Task.Delay(TimeSpan.FromSeconds(3), deadline.Token).ConfigureAwait(false);
            foreach (var id in ids)
            {
                await protocol.WriteAsync(new(MessageKind.Reply, owner, epoch, id) { Error = "Canceled" }, deadline.Token).ConfigureAwait(false);
            }
            await protocol.WriteAsync(new(MessageKind.Snapshot, owner, epoch) { Snapshot = WithTitle(current, "Released") }, deadline.Token).ConfigureAwait(false);
        }
    }

    private static MediaBackendSnapshot WithTitle(MediaBackendSnapshot snapshot, string title) => snapshot with
    {
        Revision = snapshot.Revision + 1,
        Sessions = [snapshot.Sessions[0] with { MediaProperties = snapshot.Sessions[0].MediaProperties with { Title = title } }],
    };
}