// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;
using JPSoftworks.MediaControlsExtension.Pages;
using JPSoftworks.MediaControlsExtension.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using static JPSoftworks.MediaControlsExtension.Presentation.Tests.PlaybackPresentationTestSupport;

namespace JPSoftworks.MediaControlsExtension.Presentation.Tests;

[TestClass]
public sealed class PlaybackNotificationTests
{
    [TestMethod]
    public async Task SharedViewModelPreservesCombinedSessionChangeFlags()
    {
        var backend = new FakeMediaBackend(Snapshot(1, MediaPlaybackState.Playing));
        await using var service = new MediaService(backend);
        await service.StartAsync();
        using var viewModel = new MediaSessionViewModel(service, service.CurrentSession!, NullLoggerFactory.Instance);
        var changed = new TaskCompletionSource<MediaSessionChangedEventArgs>(TaskCreationOptions
            .RunContinuationsAsynchronously);
        viewModel.Changed += (_, args) =>
        {
            if (args.Changes.HasFlag(MediaSessionChanges.PlaybackInfo))
            {
                changed.TrySetResult(args);
            }
        };

        backend.SetSnapshot(Snapshot(2, MediaPlaybackState.Paused, "New title"));
        var notification = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(notification.Changes.HasFlag(MediaSessionChanges.MediaProperties));
        Assert.AreEqual(MediaPlaybackState.Paused, viewModel.PlaybackInfo.EffectiveState);
        Assert.AreEqual("New title", viewModel.MediaProperties.Title);
    }

    [TestMethod]
    public async Task SharedViewModelForwardsOptimisticPlaybackAndStopsAfterDisposal()
    {
        var backend = new FakeMediaBackend(Snapshot(1, MediaPlaybackState.Paused));
        backend.BlockCommands();
        await using var service = new MediaService(backend);
        await service.StartAsync();
        var session = service.CurrentSession!;
        using var viewModel = new MediaSessionViewModel(service, session, NullLoggerFactory.Instance);
        var predicted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        viewModel.Changed += (_, args) =>
        {
            Interlocked.Increment(ref calls);
            if (args.Changes.HasFlag(MediaSessionChanges.PlaybackInfo) && viewModel.PlaybackInfo.IsOptimistic)
            {
                predicted.TrySetResult();
            }
        };
        try
        {
            service.TrySubmit(new(MediaCommandTarget.CurrentSession, MediaOperation.Play));
            await predicted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            viewModel.Dispose();
            var afterDisposal = Volatile.Read(ref calls);
            var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Changed += (_, _) =>
            {
                if (session.PlaybackInfo.EffectiveState == MediaPlaybackState.Paused)
                {
                    paused.TrySetResult();
                }
            };

            service.TrySubmit(new(MediaCommandTarget.CurrentSession, MediaOperation.Pause));
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(afterDisposal, Volatile.Read(ref calls));
        }
        finally
        {
            backend.ReleaseCommands();
        }
    }

    [TestMethod]
    public async Task ImmediateUpdateBypassesDebounceAndSerializesPendingUpdates()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var update = new ThrottledAction(60_000, "immediate playback test", async () =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                started.TrySetResult();
                await release.Task;
            }
            else
            {
                Assert.IsTrue(release.Task.IsCompleted);
                completed.TrySetResult();
            }
        }, NullLogger.Instance);
        try
        {
            update.Invoke();
            update.Invoke(immediately: true);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var count = 0; count < 20; count++)
            {
                update.Invoke(immediately: true);
            }

            Assert.AreEqual(1, Volatile.Read(ref calls));
            release.TrySetResult();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(2, Volatile.Read(ref calls));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [TestMethod]
    public async Task DisposalDropsAPendingImmediateUpdate()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var update = new ThrottledAction(60_000, "disposed playback test", async () =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
            finished.TrySetResult();
        }, NullLogger.Instance);
        try
        {
            update.Invoke(immediately: true);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            update.Invoke(immediately: true);
            update.Dispose();
            release.TrySetResult();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(1, Volatile.Read(ref calls));
            Assert.Throws<ObjectDisposedException>(() => update.Invoke(immediately: true));
        }
        finally
        {
            release.TrySetResult();
        }
    }
}