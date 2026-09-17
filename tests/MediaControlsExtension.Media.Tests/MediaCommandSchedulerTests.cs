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
    public async Task OutstandingPlaybackFollowsQueuedReplacementAndActiveCompletion()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new MediaCommandScheduler(async (work, token) =>
        {
            if (work.OperationId.Value == 1)
            {
                started.TrySetResult();
            }

            await release.Task.WaitAsync(token);
            Complete(work);
        }, cancellation.Token);
        try
        {
            Schedule(scheduler, CreateWork(1, 1));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = CreateWork(2, 2, new MediaBackendSessionTarget(new(1), 1));
            Schedule(scheduler, queued);
            CollectionAssert.AreEquivalent(new[] { new MediaBackendSessionTarget(new(1), 1), new(new(2), 1) },
                scheduler.GetOutstandingPlaybackTargets().ToArray());

            Schedule(scheduler, new(new(3), queued.Command with
            {
                RequestedOperation = MediaOperation.Pause,
                ResolvedOperation = MediaOperation.Pause,
                SessionsToPause = [],
            }));
            Assert.AreEqual(MediaCommandOutcomeStatus.Superseded, (await queued.Completion).Status);
            CollectionAssert.AreEquivalent(new[] { new MediaBackendSessionTarget(new(1), 1), new(new(2), 1) },
                scheduler.GetOutstandingPlaybackTargets().ToArray());

            release.TrySetResult();
            await scheduler.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsEmpty(scheduler.GetOutstandingPlaybackTargets());
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            await scheduler.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task UnconfirmedBindingsRemoveDependentPlaybackWithoutRemovingReplacementBindings()
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
            }

            Complete(work);
        }, cancellation.Token);
        try
        {
            Schedule(scheduler, CreateWork(1, 1));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var affected = CreateWork(2, 2, new MediaBackendSessionTarget(new(1), 1));
            var replacement = new MediaService.CommandWork(new(3), affected.Command with { BindingGeneration = 2 });
            var dependent = CreateWork(4, 3, new(new(1), 1), new(new(2), 1));
            var independent = CreateWork(5, 4, new MediaBackendSessionTarget(new(1), 1));
            Schedule(scheduler, affected);
            Schedule(scheduler, replacement);
            Schedule(scheduler, dependent);
            Schedule(scheduler, independent);

            var removed = scheduler.RemovePendingPlayback([new(new(2), 1)]);

            CollectionAssert.AreEquivalent(new[] { affected, dependent }, removed);
            foreach (var work in removed)
            {
                work.Complete(new(work.OperationId, MediaCommandOutcomeStatus.Abandoned, work.Command.SessionId, null));
            }

            release.TrySetResult();
            Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await replacement.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.AreEqual(MediaCommandOutcomeStatus.Completed, (await independent.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            await scheduler.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task ReplacingPendingPlaybackReleasesItsOldBindingsWithoutDependencyCycles()
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
            Schedule(scheduler, CreateWork(1, 1));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var replaced = CreateWork(2, 2, new MediaBackendSessionTarget(new(1), 1));
            Schedule(scheduler, replaced);
            var earlier = CreateWork(3, 3, new MediaBackendSessionTarget(new(2), 1));
            Schedule(scheduler, earlier);
            var latest = CreateWork(4, 2, new MediaBackendSessionTarget(new(3), 1));
            Schedule(scheduler, latest);

            Assert.AreEqual(MediaCommandOutcomeStatus.Superseded, (await replaced.Completion).Status);
            await latest.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            CollectionAssert.AreEqual(new long[] { 1, 3, 4 }, order.ToArray());
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            await scheduler.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task RepeatedPlaybackKeepsOnlyOneDeferredNativeCommand()
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
            Schedule(scheduler, CreateWork(1, 1));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var latest = CreateWork(2, 1);
            Schedule(scheduler, latest);
            for (var id = 3; id <= 1000; id++)
            {
                var next = CreateWork(id, 1);
                Schedule(scheduler, next);
                Assert.AreEqual(MediaCommandOutcomeStatus.Superseded, (await latest.Completion).Status);
                latest = next;
            }

            release.TrySetResult();
            await latest.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            CollectionAssert.AreEqual(new long[] { 1, 1000 }, order.ToArray());
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            await scheduler.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

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