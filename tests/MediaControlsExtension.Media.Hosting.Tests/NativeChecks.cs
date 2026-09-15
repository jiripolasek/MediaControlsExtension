using System.Diagnostics;
using System.Runtime.ExceptionServices;
using JPSoftworks.MediaControlsExtension.Media.Gsmtc;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting.Tests;

internal static partial class NativeChecks
{
    public static async Task RunPlayerAsync(string title)
    {
        using var player = new TestPlayer(title);
        await Task.Delay(Timeout.Infinite).ConfigureAwait(false);
    }

    public static async Task RunAsync(string directory, string executable, int minutes, int cycles, int sampleIntervalSeconds)
    {
        Directory.CreateDirectory(directory);
        using var logger = new NativeCheckLogger(directory);
        var started = Stopwatch.StartNew();
        using var process = Process.GetCurrentProcess();
        var rows = new List<string> { "cycle,elapsed_seconds,owner_handles,owner_private_bytes,worker_handles,worker_private_bytes" };
        var title = $"Media hosting acceptance {Guid.NewGuid():N}";
        using var player = new PlayerProcess(title);
        var activator = new TestSourceActivator(title);
        await CheckBackendAsync(new GsmtcBackend(NullLogger<GsmtcBackend>.Instance, activator), player.Title, activator, directory).ConfigureAwait(false);
        var index = 0;
        do
        {
            await RunWorkerAsync(executable, directory, activator, logger, async backend =>
            {
                await VerifyCommandsAsync(backend, player.Title, activator, directory).ConfigureAwait(false);
                using var worker = Process.GetProcessById(backend.WorkerProcessId);
                HostingTests.Check(backend.WorkerPackageFullName == OwnedWorkerProcess.CurrentPackageFullName, "The native worker did not inherit its owner's package identity.");
                process.Refresh();
                worker.Refresh();
                rows.Add(FormattableString.Invariant($"{index},{started.Elapsed.TotalSeconds:F2},{process.HandleCount},{process.PrivateMemorySize64},{worker.HandleCount},{worker.PrivateMemorySize64}"));
            }).ConfigureAwait(false);
            index++;
            if (index % 10 == 0)
            {
                await File.WriteAllLinesAsync(Path.Combine(directory, "native-resources.csv"), rows).ConfigureAwait(false);
                Console.WriteLine($"Native GSMTC cycles: {index}; elapsed: {started.Elapsed}.");
            }

            HostingTests.Check(!player.HasExited, "Terminating the worker also terminated its media application.");
        }
        while (index < cycles);

        if (started.Elapsed < TimeSpan.FromMinutes(minutes))
        {
            await RunWorkerAsync(executable, directory, activator, logger, async backend =>
            {
                var sample = 0;
                while (started.Elapsed < TimeSpan.FromMinutes(minutes))
                {
                    if (sample++ % 10 == 0) { await player.RestartAsync().ConfigureAwait(false); }
                    await VerifyCommandsAsync(backend, player.Title, activator, directory).ConfigureAwait(false);
                    process.Refresh();
                    using var worker = Process.GetProcessById(backend.WorkerProcessId);
                    rows.Add(FormattableString.Invariant($"{index + sample},{started.Elapsed.TotalSeconds:F2},{process.HandleCount},{process.PrivateMemorySize64},{worker.HandleCount},{worker.PrivateMemorySize64}"));
                    await File.WriteAllLinesAsync(Path.Combine(directory, "native-resources.csv"), rows).ConfigureAwait(false);
                    if (sample % 10 == 0) { Console.WriteLine($"Native GSMTC soak sample: {sample}; elapsed: {started.Elapsed}."); }
                    await Task.Delay(TimeSpan.FromSeconds(sampleIntervalSeconds)).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            HostingTests.Check(!player.HasExited, "Soak cleanup terminated the media application.");
        }

        await File.WriteAllLinesAsync(Path.Combine(directory, "native-resources.csv"), rows).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "native-result.txt"),
            FormattableString.Invariant($"PASS {index} native GSMTC worker lifetimes; {started.Elapsed.TotalMinutes:F2} minutes. Internal and hosted play, pause, next, previous, artwork and owner activation routing verified. Owner package: {OwnedWorkerProcess.CurrentPackageFullName ?? "unpackaged"}. Worker: {executable}")).ConfigureAwait(false);
    }

    private static async Task RunWorkerAsync(string executable, string directory, TestSourceActivator activator,
        NativeCheckLogger logger, Func<OutOfProcessMediaBackend, Task> check)
    {
        var owner = new MediaWorkerOwner();
        var backend = new OutOfProcessMediaBackend(new(executable, "gsmtc")
        {
            Logging = new(directory, true),
            ActivateSource = (request, token) => activator.TryActivateAsync(request.ApplicationId, request.MediaTitle, token),
        }, owner, logger);
        Exception? failure = null;
        Process? worker = null;
        try
        {
            await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await check(backend).ConfigureAwait(false);
            worker = Process.GetProcessById(backend.WorkerProcessId);
            _ = worker.Handle;
        }
        catch (Exception ex)
        {
            failure = ex;
            var snapshot = await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            logger.RecordFailure("native-primary-failure.txt", ex,
                $"Worker PID: {backend.WorkerProcessId}; epoch: {backend.WorkerEpoch}; restarts: {backend.RestartCount}; connection: {snapshot.Connection}; sessions: {snapshot.Sessions.Length}.");
        }

        try
        {
            await owner.DisposeAsync().ConfigureAwait(false);
            HostingTests.Check(worker is null || worker.HasExited, "A native worker survived owner disposal.");
        }
        catch (Exception ex)
        {
            logger.RecordFailure("native-cleanup-failure.txt", ex, $"Forced terminations: {backend.ForcedTerminationCount}.");
            failure = failure is null ? ex : new AggregateException("Native acceptance and worker cleanup failed.", failure, ex);
        }
        finally
        {
            worker?.Dispose();
        }

        if (failure is not null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
    }

    private static async Task CheckBackendAsync(GsmtcBackend backend, string title, TestSourceActivator activator, string directory)
    {
        await using (backend.ConfigureAwait(false))
        {
            await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await VerifyCommandsAsync(backend, title, activator, directory).ConfigureAwait(false);
        }
    }

    private static async Task VerifyCommandsAsync(IMediaBackend backend, string title, TestSourceActivator activator, string directory)
    {
        MediaBackendSessionSnapshot? session = null;
        await HostingTests.EventuallyAsync(async () =>
        {
            session = (await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Sessions
                .SingleOrDefault(candidate => candidate.IsAvailable && candidate.MediaProperties.Title.StartsWith(title, StringComparison.Ordinal));
            return session is not null;
        }, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        var target = session!;
        HostingTests.Check(target.Origin.TreatAsLocal, "Native playback was not local.");
        foreach (var operation in new[] { MediaOperation.Play, MediaOperation.Pause, MediaOperation.SkipNext, MediaOperation.SkipPrevious })
        {
            var result = await backend.ExecuteAsync(new(target.Id, target.BindingGeneration, operation, []), CancellationToken.None).ConfigureAwait(false);
            if (result.Status != MediaBackendCommandStatus.Completed)
            {
                var current = await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException($"Native {operation} failed: {result}; target: {target.Id}/{target.BindingGeneration}; connection: {current.Connection}.");
            }
            await HostingTests.EventuallyAsync(async () =>
            {
                backend.InvalidateObservations([new(target.Id, MediaBackendObservationChanges.Playback)]);
                var observed = (await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Sessions.Single(candidate => candidate.Id == target.Id);
                return operation switch
                {
                    MediaOperation.Play => observed.PlaybackState == MediaPlaybackState.Playing,
                    MediaOperation.Pause => observed.PlaybackState == MediaPlaybackState.Paused,
                    MediaOperation.SkipNext => observed.MediaProperties.Title == title + " next",
                    _ => observed.MediaProperties.Title == title,
                };
            }).ConfigureAwait(false);
        }

        var retriedArtwork = false;
        await HostingTests.EventuallyAsync(async () =>
        {
            var snapshot = await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var key = snapshot.Sessions.Single(candidate => candidate.Id == target.Id).MediaProperties.Artwork;
            var artwork = key is { } current ? await backend.GetArtworkAsync(current, CancellationToken.None).ConfigureAwait(false) : null;
            if (backend is OutOfProcessMediaBackend hosted)
            {
                HostingTests.Check(hosted.RestartCount == 0, "Native acceptance unexpectedly replaced its worker.");
            }
            if (artwork?.Data.Length > 0)
            {
                if (retriedArtwork)
                {
                    await File.AppendAllTextAsync(Path.Combine(directory, "artwork-retries.txt"),
                        $"{DateTime.UtcNow:O}: {backend.GetType().Name}; content received for {key}\n").ConfigureAwait(false);
                }
                return true;
            }

            // Metadata events can invalidate a captured key before its content arrives.
            retriedArtwork = true;
            var latest = await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var latestKey = latest.Sessions.SingleOrDefault(candidate => candidate.Id == target.Id)?.MediaProperties.Artwork;
            await File.AppendAllTextAsync(Path.Combine(directory, "artwork-retries.txt"),
                $"{DateTime.UtcNow:O}: {backend.GetType().Name}; requested {key}; current {latestKey}; {latest.Connection.Status}\n").ConfigureAwait(false);
            return false;
        }, TimeSpan.FromSeconds(12)).ConfigureAwait(false);
        activator.ApplicationId = target.MediaProperties.Source.NativeApplication!.ApplicationId;
        var calls = activator.Calls;
        var activation = await backend.ExecuteAsync(new(target.Id, target.BindingGeneration, MediaOperation.ActivateSource, []), CancellationToken.None).ConfigureAwait(false);
        HostingTests.Check(activation.Status == MediaBackendCommandStatus.Completed && activator.Calls == calls + 1,
            "Native source activation did not reach its owner exactly once.");
    }

    private sealed class TestSourceActivator(string title) : IGsmtcSourceActivator
    {
        private readonly int _ownerId = Environment.ProcessId;
        private int _calls;
        public string? ApplicationId { get; set; }
        public int Calls => Volatile.Read(ref this._calls);

        public Task<bool> TryActivateAsync(string applicationId, string mediaTitle, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HostingTests.Check(Environment.ProcessId == this._ownerId && applicationId == this.ApplicationId && mediaTitle.StartsWith(title, StringComparison.Ordinal),
                "Native activation carried the wrong process, application, or title.");
            Interlocked.Increment(ref this._calls);
            return Task.FromResult(true);
        }
    }

    private sealed class PlayerProcess(string title) : IDisposable
    {
        private OwnedWorkerProcess _process = Start($"{title} [0]");
        private int _generation;
        public string Title { get; private set; } = $"{title} [0]";
        public bool HasExited => this._process.HasExited;

        public async Task RestartAsync()
        {
            this._process.Terminate();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await this._process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            this._process.Dispose();
            // A new marker prevents accepting the previous process's cached snapshot.
            this.Title = $"{title} [{++this._generation}]";
            this._process = Start(this.Title);
        }

        public void Dispose() => this._process.Dispose();
        private static OwnedWorkerProcess Start(string title) => OwnedWorkerProcess.Start(Environment.ProcessPath!, ["--native-player", title]);
    }

    private sealed class TestPlayer : IDisposable
    {
        private readonly MediaPlayer _player = new();
        private readonly SystemMediaTransportControls _controls;
        private readonly InMemoryRandomAccessStream _artwork = new();
        private readonly string _title;

        public TestPlayer(string title)
        {
            this._title = title;
            this._player.CommandManager.IsEnabled = false;
            this._controls = this._player.SystemMediaTransportControls;
            this._controls.IsEnabled = true;
            this._controls.IsPlayEnabled = true;
            this._controls.IsPauseEnabled = true;
            this._controls.IsNextEnabled = true;
            this._controls.IsPreviousEnabled = true;
            this._controls.PlaybackStatus = MediaPlaybackStatus.Paused;
            this._controls.ButtonPressed += this.OnButton;
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aF9sAAAAASUVORK5CYII=");
            using (var writer = new DataWriter(this._artwork))
            {
                writer.WriteBytes(png);
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                writer.DetachStream();
            }
            this._artwork.Seek(0);
            this._controls.DisplayUpdater.Thumbnail = RandomAccessStreamReference.CreateFromStream(this._artwork);
            this.PublishTitle(title);
        }

        private void OnButton(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play: sender.PlaybackStatus = MediaPlaybackStatus.Playing; break;
                case SystemMediaTransportControlsButton.Pause: sender.PlaybackStatus = MediaPlaybackStatus.Paused; break;
                case SystemMediaTransportControlsButton.Next: this.PublishTitle(this._title + " next"); break;
                case SystemMediaTransportControlsButton.Previous: this.PublishTitle(this._title); break;
            }
        }

        private void PublishTitle(string title)
        {
            this._controls.DisplayUpdater.Type = MediaPlaybackType.Music;
            this._controls.DisplayUpdater.MusicProperties.Title = title;
            this._controls.DisplayUpdater.MusicProperties.Artist = "Controlled acceptance session";
            this._controls.DisplayUpdater.Update();
        }

        public void Dispose()
        {
            this._controls.ButtonPressed -= this.OnButton;
            this._controls.IsEnabled = false;
            this._player.Dispose();
            this._artwork.Dispose();
        }
    }
}