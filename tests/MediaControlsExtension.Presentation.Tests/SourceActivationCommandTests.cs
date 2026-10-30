// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Commands;
using JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.MediaControlsExtension.Presentation.Tests;

[TestClass]
public sealed class SourceActivationCommandTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DismissWaitsForTheCapturedActivationWithoutResubmitting(bool specificSession)
    {
        var snapshot = FakeMediaBackend.CreateSnapshot(1, "Browser");
        snapshot = snapshot with
        {
            Sessions = [snapshot.Sessions[0] with { Capabilities = MediaCapabilities.ActivateSource }],
        };
        var backend = new FakeMediaBackend(snapshot);
        backend.BlockCommands();
        await using var service = new MediaService(backend);
        await service.StartAsync();
        var sessionId = service.Sessions.Single().Id;
        var command = new BringAssociatedAppToFrontCommand(service, NullLoggerFactory.Instance,
            specificSession ? sessionId : null);
        var invocation = Task.Run(command.Invoke);
        try
        {
            await backend.CommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(invocation.IsCompleted, "CmdPal must not dismiss while activation is pending.");
            backend.ReleaseCommands();
            var result = await invocation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(CommandResultKind.Dismiss, result.Kind);
            Assert.AreEqual(MediaOperation.ActivateSource, backend.Commands.Single().Operation);
            Assert.AreEqual(sessionId, service.Sessions.Single().Id);
        }
        finally
        {
            backend.ReleaseCommands();
            await invocation.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}