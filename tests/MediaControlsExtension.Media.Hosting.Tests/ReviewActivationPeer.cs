using System.IO.Pipes;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting.Tests;

internal static class ReviewActivationPeer
{
    public static async Task RunAsync(string pipeName, Guid owner, int processId)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        PipeProtocol.VerifyPeer(pipe, processId, server: false);
        using var protocol = new PipeProtocol(pipe);
        var hello = await protocol.ReadAsync(deadline.Token).ConfigureAwait(false);
        var epoch = Guid.NewGuid();
        await protocol.WriteAsync(new(MessageKind.Welcome, owner, epoch) { CanActivateSource = hello.CanActivateSource }, deadline.Token).ConfigureAwait(false);
        var context = new HostedBackendContext(true, null, (_, _) => Task.FromResult(true));
        await using var backend = new SyntheticBackend("activation", context);
        var snapshot = await backend.ReadSnapshotAsync(default).ConfigureAwait(false);
        await protocol.WriteAsync(new(MessageKind.Snapshot, owner, epoch) { Snapshot = snapshot }, deadline.Token).ConfigureAwait(false);
        WireMessage? activationCommand = null;
        while (true)
        {
            var request = await protocol.ReadAsync(deadline.Token).ConfigureAwait(false);
            if (request.Kind == MessageKind.Shutdown) { return; }
            if (request.Kind == MessageKind.Execute && request.Command?.Operation == MediaOperation.ActivateSource)
            {
                activationCommand = request;
                var session = snapshot.Sessions[0];
                snapshot = snapshot with { Revision = snapshot.Revision + 1, Sessions = [session with { MediaProperties = session.MediaProperties with { Title = "Admitted" } }] };
                await protocol.WriteAsync(new(MessageKind.Snapshot, owner, epoch) { Snapshot = snapshot }, deadline.Token).ConfigureAwait(false);
            }
            else if (request.Kind == MessageKind.Cancel && activationCommand is { Command: { } command } admitted && request.RequestId == admitted.RequestId)
            {
                var late = new WireMessage(MessageKind.ActivateSource, owner, epoch, admitted.RequestId)
                {
                    Activation = new(admitted.RequestId, new(command.SessionId, command.BindingGeneration), "spike.player", "Initial"),
                };
                await protocol.WriteAsync(late, deadline.Token).ConfigureAwait(false);
                await protocol.WriteAsync(new(MessageKind.Reply, owner, epoch, admitted.RequestId)
                {
                    Result = new(MediaBackendCommandStatus.Failed, "Canceled"),
                }, deadline.Token).ConfigureAwait(false);
                await protocol.WriteAsync(late, deadline.Token).ConfigureAwait(false);
            }
            else if (request.Kind == MessageKind.Execute)
            {
                await protocol.WriteAsync(new(MessageKind.Reply, owner, epoch, request.RequestId)
                {
                    Result = new(MediaBackendCommandStatus.Completed, null),
                }, deadline.Token).ConfigureAwait(false);
            }
        }
    }
}