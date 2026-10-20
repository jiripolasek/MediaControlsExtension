// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Storage.Streams;
using static JPSoftworks.MediaControlsExtension.Presentation.Tests.PlaybackPresentationTestSupport;

namespace JPSoftworks.MediaControlsExtension.Presentation.Tests;

[TestClass]
public sealed class MediaSessionViewModelArtworkTests
{
    private static readonly MediaArtworkContent OriginalArtwork = new("image/png", Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg=="), "original");
    private static readonly MediaArtworkContent ReplacementArtwork = new("image/gif", Convert.FromBase64String(
        "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7"), "replacement");

    [TestMethod]
    public async Task PublishedArtworkRemainsReadableAfterReplacement()
    {
        var backend = new FakeMediaBackend(ArtworkSnapshot(1, 1)) { Artwork = OriginalArtwork };
        await using var service = new MediaService(backend);
        await service.StartAsync();
        using var viewModel = new MediaSessionViewModel(service, service.CurrentSession!, NullLoggerFactory.Instance);
        ThumbnailInfo? original = null;
        try
        {
            original = await RequestArtworkAsync(viewModel);
            var publishedIcon = original.GetIcon();
            Assert.IsNotNull(publishedIcon);

            backend.Artwork = ReplacementArtwork;
            backend.SetSnapshot(ArtworkSnapshot(2, 2));
            await WaitUntilAsync(() => viewModel.Artwork?.Hash == ReplacementArtwork.Hash);

            var replacement = viewModel.Artwork;
            Assert.IsNotNull(replacement);
            Assert.AreNotSame(original, replacement);
            var replacementIcon = replacement.GetIcon();
            Assert.IsNotNull(replacementIcon);
            Assert.AreNotSame(publishedIcon, replacementIcon);
            await AssertArtworkReadableAsync(original, publishedIcon, OriginalArtwork);
            await AssertArtworkReadableAsync(replacement, replacementIcon, ReplacementArtwork);
        }
        finally
        {
            DisposeTestArtwork(viewModel, original);
        }
    }

    [TestMethod]
    public async Task PublishedArtworkRemainsReadableAfterClearing()
    {
        var backend = new FakeMediaBackend(ArtworkSnapshot(1, 1)) { Artwork = OriginalArtwork };
        await using var service = new MediaService(backend);
        await service.StartAsync();
        using var viewModel = new MediaSessionViewModel(service, service.CurrentSession!, NullLoggerFactory.Instance);
        ThumbnailInfo? original = null;
        try
        {
            original = await RequestArtworkAsync(viewModel);
            var publishedIcon = original.GetIcon();
            Assert.IsNotNull(publishedIcon);

            backend.Artwork = null;
            backend.SetSnapshot(ArtworkSnapshot(2, null));
            await WaitUntilAsync(() => viewModel.Artwork is null);

            Assert.IsNull(viewModel.MediaProperties.Artwork);
            Assert.IsNull(viewModel.Artwork);
            await AssertArtworkReadableAsync(original, publishedIcon, OriginalArtwork);
        }
        finally
        {
            DisposeTestArtwork(viewModel, original);
        }
    }

    [TestMethod]
    public async Task PublishedArtworkRemainsReadableAfterViewModelDisposal()
    {
        var backend = new FakeMediaBackend(ArtworkSnapshot(1, 1)) { Artwork = OriginalArtwork };
        await using var service = new MediaService(backend);
        await service.StartAsync();
        using var viewModel = new MediaSessionViewModel(service, service.CurrentSession!, NullLoggerFactory.Instance);
        ThumbnailInfo? original = null;
        try
        {
            original = await RequestArtworkAsync(viewModel);
            var publishedIcon = original.GetIcon();
            Assert.IsNotNull(publishedIcon);

            viewModel.Dispose();

            Assert.IsNull(viewModel.Artwork);
            await AssertArtworkReadableAsync(original, publishedIcon, OriginalArtwork);
        }
        finally
        {
            DisposeTestArtwork(viewModel, original);
        }
    }

    private static MediaBackendSnapshot ArtworkSnapshot(long revision, long? artworkVersion)
    {
        var snapshot = Snapshot(revision, MediaPlaybackState.Playing);
        var session = snapshot.Sessions[0];
        return snapshot with
        {
            Sessions =
            [
                session with
                {
                    MediaProperties = session.MediaProperties with
                    {
                        Artwork = artworkVersion is { } version ? new MediaArtworkKey(new(1), version) : null,
                    },
                },
            ],
        };
    }

    private static async Task<ThumbnailInfo> RequestArtworkAsync(MediaSessionViewModel viewModel)
    {
        viewModel.RequestArtwork();
        await WaitUntilAsync(() => viewModel.Artwork is not null);
        var artwork = viewModel.Artwork;
        Assert.IsNotNull(artwork);
        return artwork;
    }

    private static async Task AssertArtworkReadableAsync(
        ThumbnailInfo thumbnail,
        IconInfo publishedIcon,
        MediaArtworkContent expected)
    {
        Assert.AreEqual($"data:{expected.ContentType};base64,{Convert.ToBase64String(expected.Data.Span)}",
            await thumbnail.GetDataUriAsync().WaitAsync(TimeSpan.FromSeconds(5)));

        foreach (var iconData in new[] { publishedIcon.Light, publishedIcon.Dark })
        {
            Assert.IsNotNull(iconData.Data);
            using var stream = await iconData.Data.OpenReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual((ulong)expected.Data.Length, stream.Size);
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            var loaded = await reader.LoadAsync((uint)expected.Data.Length).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual((uint)expected.Data.Length, loaded);
            var bytes = new byte[loaded];
            reader.ReadBytes(bytes);
            CollectionAssert.AreEqual(expected.Data.ToArray(), bytes);
        }
    }

    private static void DisposeTestArtwork(MediaSessionViewModel viewModel, ThumbnailInfo? original)
    {
        var current = viewModel.Artwork;
        viewModel.Dispose();
        // No host holds these streams after the test assertions finish.
        current?.Stream?.Dispose();
        if (!ReferenceEquals(current, original))
        {
            original?.Stream?.Dispose();
        }
    }
}