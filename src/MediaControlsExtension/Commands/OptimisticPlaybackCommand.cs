// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using Windows.Media.Control;
using MediaService = JPSoftworks.MediaControlsExtension.Services.MediaService;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class OptimisticPlaybackCommand : AsyncInvokableCommand
{
    private readonly Lock _presentationLock = new();
    private readonly MediaService _mediaService;
    private readonly PlayPauseMop _operation;
    private readonly YetAnotherHelper _yetAnotherHelper;

    private PlaybackIntent _intent = PlaybackIntent.Toggle;
    private MediaSource? _target;

    public OptimisticPlaybackCommand(
        MediaService mediaService,
        SettingsManager settingsManager,
        YetAnotherHelper yetAnotherHelper)
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(settingsManager);
        ArgumentNullException.ThrowIfNull(yetAnotherHelper);

        this._mediaService = mediaService;
        this._operation = new(settingsManager);
        this._yetAnotherHelper = yetAnotherHelper;
        this.Icon = Icons.PlayPause;
        this.Name = Strings.TogglePlayPause!;
    }

    public PlaybackActionPresentation UpdatePresentation(MediaSource? target, bool showName = true)
    {
        var presentation = PlaybackActionPolicy.GetPresentation(target);
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

    private async Task<ICommandResult> InvokeAsync(
        MediaSource? target,
        PlaybackIntent intent,
        CancellationToken cancellationToken)
    {
        if (target == null)
        {
            return this._yetAnotherHelper.GetMediaCommandResult($"😢 {Strings.Toast_NoCurrentSession}");
        }

        var prediction = target.BeginPlaybackPrediction(intent);
        if (prediction == null)
        {
            return this._yetAnotherHelper.GetMediaCommandResult($"😢 {Strings.Toast_NoCurrentSession}");
        }

        MediaSessionOperationResult result;
        try
        {
            var manager = this._mediaService.SessionManager;
            result = await GsmtcOperationGate.RunAsync(
                _ => this._operation.InvokeAsync(manager, target.Session, intent),
                cancellationToken);
        }
        catch (GsmtcCircuitOpenException)
        {
            result = new($"🚫 {Strings.Toast_MediaControlsUnavailable}", false);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
            result = new($"😢 {Strings.Toast_NothingHappened}", false);
        }

        target.CompletePlaybackPrediction(prediction.Value, result.Success);
        target.Update();
        return this._yetAnotherHelper.GetMediaCommandResult(result.Message);
    }
}