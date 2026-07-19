// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class ChangeVolumeMediaInvokableCommand : AsyncInvokableCommand
{
    private readonly VolumeChange _change;
    private readonly SystemVolumeService _systemVolumeService;
    private readonly YetAnotherHelper _yetAnotherHelper;

    public ChangeVolumeMediaInvokableCommand(
        VolumeChange change,
        SystemVolumeService systemVolumeService,
        YetAnotherHelper yetAnotherHelper)
    {
        this._change = change;
        this._systemVolumeService = systemVolumeService;
        this._yetAnotherHelper = yetAnotherHelper;
        this.Name = change == VolumeChange.Increase ? Strings.Command_VolumeUp! : Strings.Command_VolumeDown!;
        this.Icon = change == VolumeChange.Increase ? Icons.Volume_Max : Icons.Volume_Low;
    }

    protected override Task<ICommandResult> InvokeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var state = this._systemVolumeService.ChangeVolume(this._change, cancellationToken);
            var message = $"{Strings.Toast_Volume}: {VolumePresentation.FormatLevel(state.VolumePercent)}";
            return Task.FromResult(this._yetAnotherHelper.GetMediaCommandResult($"{(state.IsMuted ? "🔇" : "🔊")} {message}"));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }

        return Task.FromResult(this._yetAnotherHelper.GetMediaCommandResult(Strings.Toast_CantChangeVolume!));
    }
}
