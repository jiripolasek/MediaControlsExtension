// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Concurrent;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class MediaCommandSchedulerTests
{
    [TestMethod]
    public async Task WaitingSharedCommandsKeepTheirOrderWhileIndependentCommandsPass()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new ConcurrentQueue<long>();
        var scheduler = new MediaCommandScheduler(async (work, token) =>
        {
            order.Enqueue(work.OperationId.Value);
            if (work.OperationId.Value == 1)
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
            }

            Complete(work);
        }, cancellation.Token);
        try
        {
            var first = CreateWork(1, 1);
            Schedule(scheduler, first);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var shared = CreateWork(2, 2, new MediaBackendSessionTarget(new(1), 1));
            Schedule(scheduler, shared);
            var later = CreateWork(3, 1);
            Schedule(scheduler, later);
            var independent = CreateWork(4, 3);
            Schedule(scheduler, independent);
            await independent.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(first.Completion.IsCompleted);
            release.TrySetResult();
            await Task.WhenAll(first.Completion, shared.Completion, later.Completion).WaitAsync(TimeSpan.FromSeconds(5));
            CollectionAssert.AreEqual(new long[] { 1, 4, 2, 3 }, order.ToArray());
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            await scheduler.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task AdmissionIsGloballyBoundedAndShutdownCancelsUnpublishedCommands()
    {
        using var cancellation = new CancellationTokenSource();
        var executions = 0;
        var scheduler = new MediaCommandScheduler((work, _) =>
        {
            Interlocked.Increment(ref executions);
            Complete(work);
            return Task.CompletedTask;
        }, cancellation.Token);
        var commands = Enumerable.Range(1, MediaCommandScheduler.MaximumOutstandingCommands)
            .Select(static id => CreateWork(id, id)).ToArray();
        try
        {
            foreach (var work in commands)
            {
                Assert.IsTrue(scheduler.TrySchedule(work));
            }

            Assert.IsFalse(scheduler.TrySchedule(CreateWork(commands.Length + 1, commands.Length + 1)));
        }
        finally
        {
            cancellation.Cancel();
            await scheduler.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        var outcomes = await Task.WhenAll(commands.Select(static work => work.Completion)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(outcomes.All(static outcome => outcome.Status == MediaCommandOutcomeStatus.Canceled));
        Assert.AreEqual(0, Volatile.Read(ref executions));
        Assert.IsFalse(scheduler.TrySchedule(CreateWork(100, 100)));
    }

    [TestMethod]
    public async Task FailedCommandReleasesItsDependentCommand()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new MediaCommandScheduler(async (work, token) =>
        {
            if (work.OperationId.Value == 1)
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
                throw new InvalidOperationException("Injected execution failure.");
            }

            Complete(work);
        }, cancellation.Token);
        try
        {
            var first = CreateWork(1, 1);
            Schedule(scheduler, first);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var next = CreateWork(2, 1);
            Schedule(scheduler, next);
            release.TrySetResult();
            Assert.AreEqual(MediaCommandOutcomeStatus.Failed, (await first.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await next.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            await scheduler.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task ReplacementBindingDoesNotWaitForThePreviousBinding()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new MediaCommandScheduler(async (work, token) =>
        {
            if (work.Command.BindingGeneration == 1)
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
            }

            Complete(work);
        }, cancellation.Token);
        try
        {
            var original = CreateWork(1, 1);
            Schedule(scheduler, original);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var replacement = new MediaService.CommandWork(new(2), original.Command with { BindingGeneration = 2 });
            Schedule(scheduler, replacement);
            Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await replacement.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.IsFalse(original.Completion.IsCompleted);
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            await scheduler.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static MediaService.CommandWork CreateWork(long operationId, long sessionId, params MediaBackendSessionTarget[] secondaryTargets) => new(
        new(operationId),
        new(MediaOperation.Play, MediaOperation.Play, new(sessionId), new(sessionId), 1, [.. secondaryTargets], []));

    private static void Schedule(MediaCommandScheduler scheduler, MediaService.CommandWork work)
    {
        Assert.IsTrue(scheduler.TrySchedule(work));
        work.AllowExecution();
    }

    private static void Complete(MediaService.CommandWork work) =>
        work.Complete(new(work.OperationId, MediaCommandOutcomeStatus.Completed, work.Command.SessionId, null));
}