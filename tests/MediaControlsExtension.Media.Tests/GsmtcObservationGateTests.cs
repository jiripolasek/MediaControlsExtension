// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Infrastructure.Gsmtc;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class GsmtcObservationGateTests
{
    [TestMethod]
    public async Task TimedOutObservationReleasesExistingWaitersAndRecovers()
    {
        var gate = new GsmtcObservationGate(
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(250),
            TimeSpan.Zero);
        var operationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOperation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var waiterInvocations = 0;
        var blockingOperation = gate.RunAsync(
            async () =>
            {
                operationStarted.TrySetResult();
                await releaseOperation.Task.ConfigureAwait(false);
                return true;
            },
            "BlockingObservation",
            CancellationToken.None);
        await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var waiters = Enumerable.Range(0, 3)
            .Select(index => gate.RunAsync(
                () =>
                {
                    Interlocked.Increment(ref waiterInvocations);
                    return Task.FromResult(index);
                },
                $"QueuedObservation{index}",
                CancellationToken.None))
            .ToArray();

        try
        {
            await Assert.ThrowsAsync<GsmtcObservationBlockedException>(
                async () => await blockingOperation);
            foreach (var waiter in waiters)
            {
                await Assert.ThrowsAsync<GsmtcObservationBlockedException>(
                    async () => await waiter.WaitAsync(TimeSpan.FromSeconds(1)));
            }

            Assert.AreEqual(0, Volatile.Read(ref waiterInvocations));
            Assert.IsTrue(gate.IsBlocked);
        }
        finally
        {
            releaseOperation.TrySetResult();
        }

        await WaitUntilAsync(() => !gate.IsBlocked);
        var result = await gate.RunAsync(
            () => Task.FromResult(42),
            "RecoveredObservation",
            CancellationToken.None);
        Assert.AreEqual(42, result);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }
}