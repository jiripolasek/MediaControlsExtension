// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media;
namespace JPSoftworks.MediaControlsExtension.Commands;

/// <summary>
/// Submits playback toggles through the media service's predicted playback state.
/// </summary>
/// <remarks>
/// This command is not currently optimistic at the presentation boundary. The
/// service applies its prediction synchronously, but the command's icon and its
/// owning item wait for an asynchronous session notification followed by a
/// 100-150 ms presentation debounce. A prompt GSMTC confirmation can restart
/// that debounce and extend the visible delay.
///
/// A command-owned synchronous presentation event is not a safe fix by itself:
/// it would create a second state-delivery path with ordering races, update only
/// the invoking surface, introduce reentrancy and duplicate reconciliation work,
/// require careful lifetime management, and still need to handle rollback flashes
/// and stale intent during rapid input. Prefer carrying session change flags
/// through the view model, bypassing the debounce only for playback changes, and
/// deriving command feedback from the post-submission predicted state.
/// </remarks>
internal sealed partial class OptimisticPlaybackCommand : AsyncInvokableCommand
{
    private readonly Lock _presentationLock = new();
    private readonly IMediaService _mediaService;
    private readonly MediaCommandResultFactory _resultFactory;
    private readonly IIconService _iconService;
    private readonly IconSurface _iconSurface;

    private PlaybackTarget? _target;

    public OptimisticPlaybackCommand(
        IMediaService mediaService,
        MediaCommandResultFactory resultFactory,
        IIconService iconService,
        IconSurface iconSurface)
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(resultFactory);
        ArgumentNullException.ThrowIfNull(iconService);

        this._mediaService = mediaService;
        this._resultFactory = resultFactory;
        this._iconService = iconService;
        this._iconSurface = iconSurface;
        this.Icon = iconService.GetIcon(ThemedIcon.PlayPause, iconSurface);
        this.Name = Strings.TogglePlayPause!;
    }

    public PlaybackActionPresentation UpdatePresentation(MediaSession? target, bool showName = true)
    {
        var presentation = PlaybackActionPolicy.GetPresentation(
            target,
            this._iconService,
            this._iconSurface);
        lock (this._presentationLock)
        {
            this._target = target is null
                ? null
                : new(
                    target.Id,
                    target.MediaProperties.Application.ApplicationId,
                    presentation.Intent);
            this.Name = showName ? presentation.CommandName : string.Empty;
            this.UpdateIcon(presentation.CommandIcon);
        }

        return presentation;
    }

    protected override Func<ExtensionOperationDiagnostics, CancellationToken, Task<ICommandResult>> CreateInvocation()
    {
        PlaybackTarget? target;
        lock (this._presentationLock)
        {
            target = this._target;
        }

        return (diagnostics, cancellationToken) =>
            this.InvokeAsync(target, diagnostics, cancellationToken);
    }

    protected override Task<ICommandResult> InvokeAsync(CancellationToken cancellationToken)
    {
        PlaybackTarget? target;
        lock (this._presentationLock)
        {
            target = this._target;
        }

        return this.InvokeAsync(target, diagnostics: null, cancellationToken);
    }

    private Task<ICommandResult> InvokeAsync(
        PlaybackTarget? target,
        ExtensionOperationDiagnostics? diagnostics,
        CancellationToken cancellationToken)
    {
        diagnostics?.SetStage("validating optimistic playback target");
        if (target == null)
        {
            return Task.FromResult(this._resultFactory.Create($"😢 {Strings.Toast_NoCurrentSession}"));
        }

        diagnostics?.SetStage(
            $"submitting playback toggle for {target.Value.ApplicationId}");
        var submission = this._mediaService.TrySubmit(new(
            MediaCommandTarget.ForSession(target.Value.SessionId),
            MediaOperation.TogglePlayback));
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

        var message = target.Value.Intent switch
        {
            PlaybackIntent.Play => $"⏯️ {Strings.Toast_Playing}",
            PlaybackIntent.Stop => $"⏹️ {Strings.Command_Stop}",
            _ => $"⏸️ {Strings.Toast_Paused}",
        };
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
        string ApplicationId,
        PlaybackIntent Intent);
}
