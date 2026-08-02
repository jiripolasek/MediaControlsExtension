// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;

namespace JPSoftworks.MediaControlsExtension.Media;

public interface IMediaService : IDisposable, IAsyncDisposable
{
    event EventHandler? SessionsChanged;

    event EventHandler? CurrentSessionChanged;

    event EventHandler? StatusChanged;

    ImmutableArray<MediaSession> Sessions { get; }

    MediaSession? CurrentSession { get; }

    MediaServiceStatus Status { get; }

    MediaControlAvailability Availability { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    MediaCommandSubmission TrySubmit(MediaCommand command);

    ValueTask<MediaArtworkContent?> GetArtworkAsync(
        MediaArtworkKey key,
        CancellationToken cancellationToken = default);

    void UpdateOptions(MediaServiceOptions options);
}