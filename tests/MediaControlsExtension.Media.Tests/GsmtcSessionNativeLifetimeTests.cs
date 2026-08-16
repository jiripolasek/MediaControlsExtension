// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Infrastructure.Gsmtc;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class GsmtcSessionNativeLifetimeTests
{
    [TestMethod]
    public void PlaybackObjectsAreReplacedOnlyWhenTheActiveUseCommitsTheirReplacement()
    {
        var lifetime = new GsmtcSessionNativeLifetime();
        var firstPlaybackInfo = new object();
        var firstPlaybackControls = new object();
        using (var firstUse = lifetime.TryEnter()
            ?? throw new AssertFailedException("The first native use was rejected."))
        {
            firstUse.CommitPlaybackObjects(firstPlaybackInfo, firstPlaybackControls);
        }

        using var replacementUse = lifetime.TryEnter()
            ?? throw new AssertFailedException("The replacement native use was rejected.");
        Assert.AreSame(firstPlaybackInfo, lifetime.RetainedPlaybackInfo);
        Assert.AreSame(firstPlaybackControls, lifetime.RetainedPlaybackControls);

        var replacementPlaybackInfo = new object();
        var replacementPlaybackControls = new object();
        replacementUse.CommitPlaybackObjects(
            replacementPlaybackInfo,
            replacementPlaybackControls);

        Assert.AreSame(replacementPlaybackInfo, lifetime.RetainedPlaybackInfo);
        Assert.AreSame(replacementPlaybackControls, lifetime.RetainedPlaybackControls);
    }

    [TestMethod]
    public void ObservationObjectGroupsAreRetainedIndependently()
    {
        var lifetime = new GsmtcSessionNativeLifetime();
        using var nativeUse = lifetime.TryEnter()
            ?? throw new AssertFailedException("The native use was rejected.");
        var playbackInfo = new object();
        var playbackControls = new object();
        var timelineProperties = new object();
        var mediaProperties = new object();
        var thumbnail = new object();
        var genres = new object();

        nativeUse.CommitPlaybackObjects(playbackInfo, playbackControls);
        nativeUse.CommitTimelineObjects(timelineProperties);
        nativeUse.CommitMediaObjects(mediaProperties, thumbnail, genres);

        Assert.AreSame(playbackInfo, lifetime.RetainedPlaybackInfo);
        Assert.AreSame(playbackControls, lifetime.RetainedPlaybackControls);
        Assert.AreSame(timelineProperties, lifetime.RetainedTimelineProperties);
        Assert.AreSame(mediaProperties, lifetime.RetainedMediaProperties);
        Assert.AreSame(thumbnail, lifetime.RetainedThumbnail);
        Assert.AreSame(genres, lifetime.RetainedGenres);

        var replacementTimeline = new object();
        nativeUse.CommitTimelineObjects(replacementTimeline);

        Assert.AreSame(playbackInfo, lifetime.RetainedPlaybackInfo);
        Assert.AreSame(replacementTimeline, lifetime.RetainedTimelineProperties);
        Assert.AreSame(mediaProperties, lifetime.RetainedMediaProperties);
    }

    [TestMethod]
    public void CommandPlaybackObjectsDoNotReplaceObservationPlaybackObjects()
    {
        var lifetime = new GsmtcSessionNativeLifetime();
        using var nativeUse = lifetime.TryEnter()
            ?? throw new AssertFailedException("The native use was rejected.");
        var observationPlaybackInfo = new object();
        var observationPlaybackControls = new object();
        var commandPlaybackInfo = new object();
        var commandPlaybackControls = new object();

        nativeUse.CommitPlaybackObjects(
            observationPlaybackInfo,
            observationPlaybackControls);
        nativeUse.CommitCommandPlaybackObjects(
            commandPlaybackInfo,
            commandPlaybackControls);

        Assert.AreSame(observationPlaybackInfo, lifetime.RetainedPlaybackInfo);
        Assert.AreSame(observationPlaybackControls, lifetime.RetainedPlaybackControls);
        Assert.AreSame(commandPlaybackInfo, lifetime.RetainedCommandPlaybackInfo);
        Assert.AreSame(commandPlaybackControls, lifetime.RetainedCommandPlaybackControls);

        var replacementObservationPlaybackInfo = new object();
        var replacementObservationPlaybackControls = new object();
        nativeUse.CommitPlaybackObjects(
            replacementObservationPlaybackInfo,
            replacementObservationPlaybackControls);

        Assert.AreSame(replacementObservationPlaybackInfo, lifetime.RetainedPlaybackInfo);
        Assert.AreSame(replacementObservationPlaybackControls, lifetime.RetainedPlaybackControls);
        Assert.AreSame(commandPlaybackInfo, lifetime.RetainedCommandPlaybackInfo);
        Assert.AreSame(commandPlaybackControls, lifetime.RetainedCommandPlaybackControls);
    }

    [TestMethod]
    public async Task RetirementRejectsNewUsesAndWaitsForTheActiveUse()
    {
        var lifetime = new GsmtcSessionNativeLifetime();
        var activeUse = lifetime.TryEnter()
            ?? throw new AssertFailedException("The native use was rejected.");
        activeUse.CommitPlaybackObjects(new object(), new object());
        var nativeStateRetired = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var retirement = lifetime.RetireAsync(() =>
        {
            nativeStateRetired.TrySetResult();
            return Task.CompletedTask;
        });

        Assert.IsTrue(lifetime.IsRetiring);
        Assert.IsNull(lifetime.TryEnter());
        Assert.IsFalse(nativeStateRetired.Task.IsCompleted);
        Assert.IsFalse(retirement.IsCompleted);

        activeUse.Dispose();

        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(nativeStateRetired.Task.IsCompletedSuccessfully);
        Assert.AreEqual(0, lifetime.ActiveUseCount);
        Assert.IsNull(lifetime.RetainedPlaybackInfo);
        Assert.IsNull(lifetime.RetainedPlaybackControls);
        Assert.IsNull(lifetime.RetainedCommandPlaybackInfo);
        Assert.IsNull(lifetime.RetainedCommandPlaybackControls);
    }

    [TestMethod]
    public async Task TimedOutObservationKeepsRetirementWaitingForItsActualCompletion()
    {
        var lifetime = new GsmtcSessionNativeLifetime();
        var gate = new GsmtcObservationGate(
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.Zero);
        var operationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOperation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeStateRetired = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observation = gate.RunAsync(
            async () =>
            {
                using var nativeUse = lifetime.TryEnter()
                    ?? throw new GsmtcSessionRetiredException();
                operationStarted.TrySetResult();
                await releaseOperation.Task.ConfigureAwait(false);
                return true;
            },
            "LifetimeTest",
            CancellationToken.None);
        await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var retirement = lifetime.RetireAsync(() =>
        {
            nativeStateRetired.TrySetResult();
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<GsmtcObservationBlockedException>(
            async () => await observation);
        Assert.IsFalse(nativeStateRetired.Task.IsCompleted);
        Assert.IsFalse(retirement.IsCompleted);

        releaseOperation.TrySetResult();

        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(nativeStateRetired.Task.IsCompletedSuccessfully);
    }
}