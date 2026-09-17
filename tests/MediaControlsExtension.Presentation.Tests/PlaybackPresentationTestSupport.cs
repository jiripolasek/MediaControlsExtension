// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Collections.Immutable;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Presentation.Tests
{
    internal static class PlaybackPresentationTestSupport
    {
        public static MediaBackendSnapshot Snapshot(long revision, MediaPlaybackState state, string title = "Track")
        {
            var snapshot = FakeMediaBackend.CreateSnapshot(revision, title, playbackState: state);
            var session = snapshot.Sessions[0];
            return snapshot with
            {
                Sessions =
                [
                    session with
                    {
                        MediaProperties = session.MediaProperties with
                        {
                            Source = session.MediaProperties.Source with { NativeApplication = null },
                        },
                    }
                ],
            };
        }

        public static async Task WaitUntilAsync(Func<bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!predicate())
            {
                await Task.Delay(10, timeout.Token);
            }
        }
    }

    internal sealed class PlaybackTestIcons : IIconService
    {
        public event EventHandler? IconsChanged { add { } remove { } }
        public IReadOnlyList<IconThemeInfo> Themes => [];
        public IReadOnlyList<IconThemeDiagnostic> Diagnostics => [];

        public IconInfo GetIcon(ThemedIcon icon, IconSurface surface, IconState state = IconState.Default) =>
            new(icon.ToString());

        public void Dispose() { }
    }

    internal sealed class PlaybackTestSettings : ISettingsManager
    {
        public bool ShowThumbnails => false;
        public bool ShowDetails => false;
        public bool ShowTrackNavigationCommandsAtTopLevel => false;
        public bool KeepOpen => true;
        public bool KeepOpenTogglePlayPauseCurrent => true;
        public bool KeepOpenSkipTrack => true;
        public bool KeepOpenTogglePlayMedia => true;
        public bool ShowToastMessages => true;
        public bool PauseOthersOnPlay => false;
        public bool ShowCurrentMediaAtTopLevel => true;
        public bool EnableVolumeControls => false;
        public bool ShowSkipCommands => false;
        public bool ShowSkipCommandsInDockBand => false;
        public bool ShowVolumeAdjustmentCommandsInDockBand => false;
        public DockCurrentMediaActionMode DockCurrentMediaAction => DockCurrentMediaActionMode.Default;
        public string CommandPaletteIconThemeId => "default";
        public string DockIconThemeId => "default";
    }

    internal sealed class RecordingPlaybackService(IMediaService inner) : IMediaService
    {
        public event EventHandler? BackendsChanged
        {
            add => inner.BackendsChanged += value;
            remove => inner.BackendsChanged -= value;
        }

        public event EventHandler? CurrentSessionChanged
        {
            add => inner.CurrentSessionChanged += value;
            remove => inner.CurrentSessionChanged -= value;
        }

        public event EventHandler? SessionsChanged
        {
            add => inner.SessionsChanged += value;
            remove => inner.SessionsChanged -= value;
        }

        public event EventHandler? StatusChanged
        {
            add => inner.StatusChanged += value;
            remove => inner.StatusChanged -= value;
        }

        public List<MediaCommand> Commands { get; } = [];
        public List<MediaCommandSubmission> Submissions { get; } = [];
        public Action? AfterSubmit { get; set; }
        public MediaCommandOutcome? ImmediateOutcome { get; set; }
        public ImmutableArray<MediaBackendState> Backends => inner.Backends;
        public ImmutableArray<MediaSession> Sessions => inner.Sessions;
        public MediaSession? CurrentSession => inner.CurrentSession;
        public MediaServiceStatus Status => inner.Status;
        public MediaControlAvailability Availability => inner.Availability;
        public Task StartAsync(CancellationToken cancellationToken = default) => inner.StartAsync(cancellationToken);

        public MediaCommandSubmission TrySubmit(MediaCommand command)
        {
            this.Commands.Add(command);
            var submission = this.ImmediateOutcome is { } outcome
                ? new(MediaCommandSubmissionStatus.Accepted, outcome.OperationId, 1, Task.FromResult(outcome))
                : inner.TrySubmit(command);
            this.Submissions.Add(submission);
            this.AfterSubmit?.Invoke();
            return submission;
        }

        public ValueTask<MediaArtworkContent?> GetArtworkAsync(
            MediaArtworkKey key,
            CancellationToken cancellationToken) =>
            inner.GetArtworkAsync(key, cancellationToken);

        public void UpdateOptions(MediaServiceOptions options) => inner.UpdateOptions(options);
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

namespace JPSoftworks.MediaControlsExtension.Helpers
{
    // Playback fixtures provide source presentation without native application lookup.
    internal static class AppInfoResolver
    {
        public static IAppInfo Resolve(string applicationId) =>
            throw new InvalidOperationException($"Unexpected native application lookup: {applicationId}");
    }
}