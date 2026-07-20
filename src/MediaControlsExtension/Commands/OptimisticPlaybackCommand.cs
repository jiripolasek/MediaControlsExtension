// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using MediaService = JPSoftworks.MediaControlsExtension.Services.MediaService;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class OptimisticPlaybackCommand : AsyncInvokableCommand
{
    private readonly Lock _presentationLock = new();
    private readonly MediaService _mediaService;
    private readonly PlayPauseMop _operation;
    private readonly YetAnotherHelper _yetAnotherHelper;
    private readonly IIconService _iconService;
    private readonly IconSurface _iconSurface;

    private PlaybackIntent _intent = PlaybackIntent.Toggle;
    private MediaSource? _target;

    public OptimisticPlaybackCommand(
        MediaService mediaService,
        SettingsManager settingsManager,
        YetAnotherHelper yetAnotherHelper,
        IIconService iconService,
        IconSurface iconSurface)
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(settingsManager);
        ArgumentNullException.ThrowIfNull(yetAnotherHelper);
        ArgumentNullException.ThrowIfNull(iconService);

        this._mediaService = mediaService;
        this._operation = new(settingsManager);
        this._yetAnotherHelper = yetAnotherHelper;
        this._iconService = iconService;
        this._iconSurface = iconSurface;
        this.Icon = iconService.GetIcon(ThemedIcon.PlayPause, iconSurface);
        this.Name = Strings.TogglePlayPause!;
    }

    public PlaybackActionPresentation UpdatePresentation(MediaSource? target, bool showName = true)
    {
        var presentation = PlaybackActionPolicy.GetPresentation(
            target,
            this._iconService,
            this._iconSurface);
        lock (this._presentationLock)
        {
            this._target = target;
            this._intent = presentation.Intent;
            this.Name = showName ? presentation.CommandName : string.Empty;
            this.UpdateIcon(presentation.CommandIcon);
        }

        return presentation;
    }

    protected override Func<CancellationToken, Task<ICommandResult>> CreateInvocation()
    {
        MediaSource? target;
        PlaybackIntent intent;
        lock (this._presentationLock)
        {
            target = this._target;
            intent = this._intent;
        }

        return cancellationToken => this.InvokeAsync(target, intent, cancellationToken);
    }

    protected override Task<ICommandResult> InvokeAsync(CancellationToken cancellationToken)
    {
        return this.CreateInvocation()(cancellationToken);
    }

    private Task<ICommandResult> InvokeAsync(
        MediaSource? target,
        PlaybackIntent intent,
        CancellationToken cancellationToken)
    {
        if (target == null)
        {
            return Task.FromResult(this._yetAnotherHelper.GetMediaCommandResult($"😢 {Strings.Toast_NoCurrentSession}"));
        }

        if (GsmtcOperationGate.IsCircuitOpen)
        {
            return Task.FromResult(this._yetAnotherHelper.GetMediaCommandResult($"🚫 {Strings.Toast_MediaControlsUnavailable}"));
        }

        // The press only records the desired end state and returns; the
        // source's playback queue coalesces rapid presses and executes the
        // trailing action, so spamming the button can neither back up the
        // GSMTC gate nor resolve against a stale playback snapshot.
        var mediaService = this._mediaService;
        var operation = this._operation;
        var queuedIntent = target.EnqueuePlaybackAction(
            intent,
            async (session, absoluteIntent) =>
            {
                if (!mediaService.TryGetSessionManager(out var manager))
                {
                    return false;
                }

                var result = await operation.ExecuteAsync(manager, session, absoluteIntent);
                return result.Success;
            });

        if (queuedIntent is null)
        {
            return Task.FromResult(this._yetAnotherHelper.GetMediaCommandResult($"😢 {Strings.Toast_NoCurrentSession}"));
        }

        var message = queuedIntent switch
        {
            PlaybackIntent.Play => $"⏯️ {Strings.Toast_Playing}",
            PlaybackIntent.Stop => $"⏹️ {Strings.Command_Stop}",
            _ => $"⏸️ {Strings.Toast_Paused}"
        };
        return Task.FromResult(this._yetAnotherHelper.GetMediaCommandResult(message));
    }
}