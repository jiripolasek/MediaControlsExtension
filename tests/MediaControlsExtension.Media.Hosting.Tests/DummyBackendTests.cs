using System.Buffers.Binary;
using System.Diagnostics;
using JPSoftworks.MediaControlsExtension.Media.Dummy;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting.Tests;

internal static class DummyBackendTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("Dummy playlists are seeded, bounded and have valid artwork", PlaylistAsync),
        ("Dummy commands preserve bindings and fence old artwork", ControlsAsync),
        ("Dummy publishes track changes without position ticks and disposal ends observation", LifetimeAsync),
        ("Production dummy factory runs controls through a worker", () => WorkerAsync(Environment.ProcessPath!)),
        ("Dummy worker recovery leaves another backend running", IndependentWorkersAsync),
    ];

    public static async Task WorkerAsync(string executable)
    {
        await using var owner = new MediaWorkerOwner();
        await using var backend = new OutOfProcessMediaBackend(new(executable, "dummy"), owner);
        await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
        var snapshot = await HostingTests.WaitForSnapshotAsync(backend).ConfigureAwait(false);
        HostingTests.Check(snapshot.Sessions.Length == 3, "The production dummy factory did not publish three sessions.");
        HostingTests.Check(backend.WorkerPackageFullName == OwnedWorkerProcess.CurrentPackageFullName, "The dummy worker lost its package identity.");
        await ExerciseControlsAsync(backend).ConfigureAwait(false);
        using var worker = Process.GetProcessById(backend.WorkerProcessId);
        _ = worker.Handle;
        await owner.DisposeAsync().ConfigureAwait(false);
        HostingTests.Check(worker.HasExited && worker.ExitCode == 0, "The dummy worker did not exit with its owner scope.");
    }

    private static async Task PlaylistAsync()
    {
        await using var first = new DummyBackend(42);
        await using var second = new DummyBackend(42);
        await first.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await second.StartAsync(CancellationToken.None).ConfigureAwait(false);
        var a = await first.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var b = await second.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        HostingTests.Check(a.Sessions.Length == 3 && a.Sessions.Select(static session => session.Id).Distinct().Count() == 3,
            "Dummy sessions do not have distinct identities.");
        for (var i = 0; i < a.Sessions.Length; i++)
        {
            var session = a.Sessions[i];
            HostingTests.Check(session.MediaProperties.Title == b.Sessions[i].MediaProperties.Title &&
                session.TimelineProperties.Duration == b.Sessions[i].TimelineProperties.Duration,
                "A seed did not reproduce its playlist.");
            HostingTests.Check(session.MediaProperties.Source.NativeApplication is null &&
                session.MediaProperties.AlbumTrackCount == 12 && session.TimelineProperties.EndTime >= TimeSpan.FromSeconds(15) &&
                session.TimelineProperties.EndTime <= TimeSpan.FromSeconds(45), "Invalid simulated metadata.");
            var artwork = await first.GetArtworkAsync(session.MediaProperties.Artwork!.Value, CancellationToken.None).ConfigureAwait(false);
            HostingTests.Check(artwork is { ContentType: "image/bmp" } && artwork.Data.Length == 54 + 32 * 32 * 3 &&
                artwork.Data.Span[0] == 'B' && artwork.Data.Span[1] == 'M' &&
                BinaryPrimitives.ReadInt32LittleEndian(artwork.Data.Span[18..]) == 32, "Invalid dummy artwork.");
        }

        var initial = a.Sessions[0];
        for (var i = 0; i < 12; i++)
        {
            await first.ExecuteAsync(HostingTests.Command(a, MediaOperation.SkipNext), CancellationToken.None).ConfigureAwait(false);
        }
        var wrapped = (await first.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Sessions[0];
        HostingTests.Check(wrapped.MediaProperties.Title == initial.MediaProperties.Title &&
            wrapped.MediaProperties.Artwork!.Value.Version > initial.MediaProperties.Artwork!.Value.Version,
            "Playlist wrapping reused an artwork version or grew the playlist.");
    }

    private static async Task ControlsAsync()
    {
        await using var backend = new DummyBackend(7);
        await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await ExerciseControlsAsync(backend).ConfigureAwait(false);
    }

    private static async Task ExerciseControlsAsync(IMediaBackend backend)
    {
        var snapshot = await HostingTests.WaitForSnapshotAsync(backend).ConfigureAwait(false);
        var initial = snapshot.Sessions[0];
        var target = new MediaBackendSessionTarget(initial.Id, initial.BindingGeneration);
        async Task ExecuteAsync(MediaOperation operation)
        {
            var result = await backend.ExecuteAsync(new(target.SessionId, target.BindingGeneration, operation, []), CancellationToken.None).ConfigureAwait(false);
            HostingTests.Check(result.Status == MediaBackendCommandStatus.Completed, $"Dummy {operation} failed.");
        }
        async Task<MediaBackendSessionSnapshot> WaitAsync(Func<MediaBackendSessionSnapshot, bool> condition)
        {
            MediaBackendSessionSnapshot? result = null;
            await HostingTests.EventuallyAsync(async () =>
            {
                result = (await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Sessions.Single(session => session.Id == target.SessionId);
                return condition(result);
            }).ConfigureAwait(false);
            HostingTests.Check(result!.BindingGeneration == target.BindingGeneration, "A track change replaced the session binding.");
            return result;
        }

        await ExecuteAsync(MediaOperation.Pause).ConfigureAwait(false);
        var paused = await WaitAsync(static session => session.PlaybackState == MediaPlaybackState.Paused).ConfigureAwait(false);
        await Task.Delay(1100).ConfigureAwait(false);
        var stillPaused = (await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Sessions[0];
        HostingTests.Check(stillPaused.TimelineProperties.Position == paused.TimelineProperties.Position, "Paused dummy playback advanced.");
        var oldArtwork = initial.MediaProperties.Artwork!.Value;
        var content = await backend.GetArtworkAsync(oldArtwork, CancellationToken.None).ConfigureAwait(false);
        HostingTests.Check(content is not null, "Dummy artwork did not reach the owner.");
        await ExecuteAsync(MediaOperation.SkipNext).ConfigureAwait(false);
        await WaitAsync(session => session.MediaProperties.Artwork != oldArtwork).ConfigureAwait(false);
        HostingTests.Check(await backend.GetArtworkAsync(oldArtwork, CancellationToken.None).ConfigureAwait(false) is null, "An obsolete artwork key returned replacement content.");
        await ExecuteAsync(MediaOperation.SkipPrevious).ConfigureAwait(false);
        var previous = await WaitAsync(session => session.MediaProperties.Title == initial.MediaProperties.Title).ConfigureAwait(false);
        var restored = await backend.GetArtworkAsync(previous.MediaProperties.Artwork!.Value, CancellationToken.None).ConfigureAwait(false);
        HostingTests.Check(restored is not null && restored.Data.Span.SequenceEqual(content!.Data.Span), "Previous did not restore its playlist track artwork.");
        await ExecuteAsync(MediaOperation.Play).ConfigureAwait(false);
        var playing = await WaitAsync(static session => session.PlaybackState == MediaPlaybackState.Playing).ConfigureAwait(false);
        await Task.Delay(1200).ConfigureAwait(false);
        var later = (await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Sessions[0];
        HostingTests.Check(later.TimelineProperties == playing.TimelineProperties && later.TimelineProperties.LastUpdatedAt is { } timestamp &&
            DateTimeOffset.UtcNow - timestamp >= TimeSpan.FromSeconds(1), "Playing positions were republished instead of retaining their timestamped sample.");
        await ExecuteAsync(MediaOperation.Stop).ConfigureAwait(false);
        await WaitAsync(static session => session.PlaybackState == MediaPlaybackState.Stopped && session.TimelineProperties.Position == TimeSpan.Zero).ConfigureAwait(false);
        foreach (var command in new[]
        {
            new MediaBackendCommand(target.SessionId, target.BindingGeneration + 1, MediaOperation.Play, []),
            new MediaBackendCommand(new(long.MaxValue), target.BindingGeneration, MediaOperation.Play, []),
        })
        {
            HostingTests.Check((await backend.ExecuteAsync(command, CancellationToken.None).ConfigureAwait(false)).Status == MediaBackendCommandStatus.SessionGone,
                "A stale or unknown binding accepted a command.");
        }
        HostingTests.Check(!initial.Capabilities.HasFlag(MediaCapabilities.ActivateSource) &&
            (await backend.ExecuteAsync(new(target.SessionId, target.BindingGeneration, MediaOperation.ActivateSource, []), CancellationToken.None).ConfigureAwait(false)).Status == MediaBackendCommandStatus.Unsupported,
            "The dummy player offered native activation.");
    }

    private static async Task LifetimeAsync()
    {
        await using var backend = new DummyBackend(10);
        await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await using var watch = backend.WatchAsync(CancellationToken.None).GetAsyncEnumerator();
        HostingTests.Check(await watch.MoveNextAsync().ConfigureAwait(false), "Startup lost its signal.");
        var initial = await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var trackChanged = watch.MoveNextAsync().AsTask();
        await Task.Delay(1200).ConfigureAwait(false);
        HostingTests.Check(!trackChanged.IsCompleted && ReferenceEquals(initial, await backend.ReadSnapshotAsync(default).ConfigureAwait(false)),
            "A position-only timer tick published another snapshot.");
        var duration = initial.Sessions[0].TimelineProperties.EndTime;
        HostingTests.Check(await trackChanged.WaitAsync(duration + TimeSpan.FromSeconds(3)).ConfigureAwait(false), "The next track was not published.");
        var updated = await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        HostingTests.Check(updated.Revision > initial.Revision && updated.Sessions[0].MediaProperties.Artwork != initial.Sessions[0].MediaProperties.Artwork &&
            updated.Sessions[0].TimelineProperties.Position == TimeSpan.Zero && updated.Sessions[0].Id == initial.Sessions[0].Id,
            "A track change lost its update, position reset, or stable session identity.");
        var next = watch.MoveNextAsync().AsTask();
        await backend.DisposeAsync().ConfigureAwait(false);
        HostingTests.Check(!await next.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false), "Disposal left the observation stream open.");
        HostingTests.Check((await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Sessions.IsEmpty &&
            (await backend.ExecuteAsync(HostingTests.Command(initial, MediaOperation.Play), CancellationToken.None).ConfigureAwait(false)).Status == MediaBackendCommandStatus.Unavailable,
            "Disposal left dummy sessions or controls available.");
    }

    private static async Task IndependentWorkersAsync()
    {
        await using var owner = new MediaWorkerOwner();
        await using var dummy = new OutOfProcessMediaBackend(Program.Options("dummy"), owner);
        await using var other = new OutOfProcessMediaBackend(Program.Options("application-plain"), owner);
        await dummy.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await other.StartAsync(CancellationToken.None).ConfigureAwait(false);
        var before = await HostingTests.WaitForSnapshotAsync(dummy).ConfigureAwait(false);
        var otherSnapshot = await HostingTests.WaitForSnapshotAsync(other).ConfigureAwait(false);
        using var worker = Process.GetProcessById(dummy.WorkerProcessId);
        using var otherWorker = Process.GetProcessById(other.WorkerProcessId);
        _ = otherWorker.Handle;
        worker.Kill();
        await worker.WaitForExitAsync().ConfigureAwait(false);
        await HostingTests.EventuallyAsync(() => dummy.WorkerProcessId != 0 && dummy.WorkerProcessId != worker.Id).ConfigureAwait(false);
        var after = await HostingTests.WaitForSnapshotAsync(dummy).ConfigureAwait(false);
        HostingTests.Check(after.Sessions.All(session => before.Sessions.All(old => old.Id != session.Id)), "Recovery reused dummy session IDs.");
        HostingTests.Check((await dummy.ExecuteAsync(HostingTests.Command(before, MediaOperation.Play), CancellationToken.None).ConfigureAwait(false)).Status == MediaBackendCommandStatus.SessionGone,
            "A pre-crash command reached the replacement dummy.");
        HostingTests.Check(!otherWorker.HasExited && (await other.ExecuteAsync(HostingTests.Command(otherSnapshot, MediaOperation.Play), CancellationToken.None).ConfigureAwait(false)).Status == MediaBackendCommandStatus.Completed,
            "Dummy recovery affected an independent backend.");
        await dummy.DisposeAsync().ConfigureAwait(false);
        HostingTests.Check(!otherWorker.HasExited, "Disabling dummy terminated its peer.");
        await owner.DisposeAsync().ConfigureAwait(false);
        HostingTests.Check(otherWorker.HasExited, "Owner disposal left a peer worker running.");
    }
}