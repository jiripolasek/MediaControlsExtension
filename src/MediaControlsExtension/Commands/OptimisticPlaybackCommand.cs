// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media;
namespace JPSoftworks.MediaControlsExtension.Commands;

/// <summary>
/// Submits the explicit playback action captured with the command's presentation.
/// </summary>
/// <remarks>
/// Each instance retains its session and action; owners publish replacements on session notifications.
/// </remarks>
internal sealed partial class OptimisticPlaybackCommand : AsyncInvokableCommand
{
    private readonly IMediaService _mediaService;
    private readonly MediaCommandResultFactory _resultFactory;
    private readonly IIconService _iconService;
    private readonly IconSurface _iconSurface;
    private readonly ILoggerFactory _loggerFactory;
    private readonly CommandLifetime _lifetime;
    private readonly PlaybackTarget? _target;

    public PlaybackActionPresentation Presentation { get; }

    public OptimisticPlaybackCommand(
        IMediaService mediaService,
        MediaCommandResultFactory resultFactory,
        IIconService iconService,
        IconSurface iconSurface,
        ILoggerFactory loggerFactory)
        : base(loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(resultFactory);
        ArgumentNullException.ThrowIfNull(iconService);

        this._mediaService = mediaService;
        this._resultFactory = resultFactory;
        this._iconService = iconService;
        this._iconSurface = iconSurface;
        this._loggerFactory = loggerFactory;
        this._lifetime = new();
        this.Presentation = PlaybackActionPolicy.GetPresentation(null, iconService, iconSurface);
        this.Icon = this.Presentation.CommandIcon;
        this.Name = this.Presentation.CommandName;
    }

    private OptimisticPlaybackCommand(
        OptimisticPlaybackCommand previous,
        PlaybackTarget? target,
        PlaybackActionPresentation presentation,
        bool showName)
        : base(previous._loggerFactory)
    {
        this._mediaService = previous._mediaService;
        this._resultFactory = previous._resultFactory;
        this._iconService = previous._iconService;
        this._iconSurface = previous._iconSurface;
        this._loggerFactory = previous._loggerFactory;
        this._lifetime = previous._lifetime;
        this._target = target;
        this.Presentation = presentation;
        this.Id = previous.Id;
        this.Name = showName ? presentation.CommandName : string.Empty;
        this.Icon = presentation.CommandIcon;
    }

    /// <summary>Returns a command for this presentation while preserving previously published actions.</summary>
    public OptimisticPlaybackCommand WithPresentation(MediaSession? target, bool showName = true)
    {
        var presentation = PlaybackActionPolicy.GetPresentation(
            target,
            this._iconService,
            this._iconSurface);
        PlaybackTarget? playbackTarget = target is { IsAvailable: true }
            ? new(target.Id, presentation.Intent)
            : null;
        if (this._target == playbackTarget && this.Presentation.Intent == presentation.Intent &&
            this.Name == (showName ? presentation.CommandName : string.Empty) &&
            IconExtensions.HasSameIcon(this.Icon, presentation.CommandIcon))
        {
            return this;
        }

        return new(this, playbackTarget, presentation, showName);
    }

    /// <summary>Disables every command published by this owner.</summary>
    public void Disable() => Volatile.Write(ref this._lifetime.Disabled, 1);

    protected override Func<ExtensionOperationDiagnostics, CancellationToken, Task<ICommandResult>> CreateInvocation() =>
        (diagnostics, cancellationToken) => this.InvokeAsync(this._target, diagnostics, cancellationToken);

    protected override Task<ICommandResult> InvokeAsync(CancellationToken cancellationToken) =>
        this.InvokeAsync(this._target, diagnostics: null, cancellationToken);

    private Task<ICommandResult> InvokeAsync(
        PlaybackTarget? target,
        ExtensionOperationDiagnostics? diagnostics,
        CancellationToken cancellationToken)
    {
        diagnostics?.SetStage("validating optimistic playback target");
        if (target == null || Volatile.Read(ref this._lifetime.Disabled) != 0)
        {
            return Task.FromResult(this._resultFactory.Create($"😢 {Strings.Toast_NoCurrentSession}"));
        }

        var operation = target.Value.Intent switch
        {
            PlaybackIntent.Play => MediaOperation.Play,
            PlaybackIntent.Pause => MediaOperation.Pause,
            PlaybackIntent.Stop => MediaOperation.Stop,
            _ => throw new InvalidOperationException("An available playback target must have an explicit action."),
        };
        diagnostics?.SetStage(
            $"submitting {operation} for session {target.Value.SessionId.Value}");
        var submission = this._mediaService.TrySubmit(new(
            MediaCommandTarget.ForSession(target.Value.SessionId),
            operation));
        if (submission.Status != MediaCommandSubmissionStatus.Accepted)
        {
            diagnostics?.SetStage($"media service rejected command: {submission.Status}");
            var failureMessage = submission.Status switch
            {
                MediaCommandSubmissionStatus.Busy => null,
                MediaCommandSubmissionStatus.SessionGone => $"😢 {Strings.Toast_NoCurrentSession}",
                _ when RequiresRestart(this._mediaService) =>
                    $"🚫 {Strings.Toast_MediaControlsUnavailable}",
                _ => $"😢 {Strings.Toast_NothingHappened}",
            };
            return Task.FromResult(this._resultFactory.Create(failureMessage));
        }

        string? message = target.Value.Intent switch
        {
            PlaybackIntent.Play => $"⏯️ {Strings.Toast_Playing}",
            PlaybackIntent.Stop => $"⏹️ {Strings.Command_Stop}",
            _ => $"⏸️ {Strings.Toast_Paused}",
        };
        if (submission.Completion is { IsCompletedSuccessfully: true } completed)
        {
            message = completed.Result.Status == MediaCommandOutcomeStatus.Completed
                ? MediaCommandFeedback.AppendPauseWarning(message, completed.Result)
                : MediaCommandFeedback.GetWarning(completed.Result);
        }
        else
        {
            this._resultFactory.ObserveFailures(submission);
        }

        diagnostics?.SetStage("creating optimistic command result");
        return Task.FromResult(this._resultFactory.Create(message));
    }

    private static bool RequiresRestart(IMediaService mediaService)
    {
        return mediaService.Availability == MediaControlAvailability.CircuitOpen ||
               mediaService.Status == MediaServiceStatus.Faulted;
    }

    private readonly record struct PlaybackTarget(
        MediaSessionId SessionId,
        PlaybackIntent Intent);

    private sealed class CommandLifetime
    {
        public int Disabled;
    }
}