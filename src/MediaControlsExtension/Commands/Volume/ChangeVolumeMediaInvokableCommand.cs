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
    private readonly MediaCommandResultFactory _resultFactory;

    public ChangeVolumeMediaInvokableCommand(
        VolumeChange change,
        SystemVolumeService systemVolumeService,
        MediaCommandResultFactory resultFactory)
    {
        this._change = change;
        this._systemVolumeService = systemVolumeService;
        this._resultFactory = resultFactory;
        this.Name = change == VolumeChange.Increase ? Strings.Command_VolumeUp! : Strings.Command_VolumeDown!;
        this.Icon = change == VolumeChange.Increase ? Icons.Volume_Up : Icons.Volume_Down;
    }

    protected override Task<ICommandResult> InvokeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var state = this._systemVolumeService.ChangeVolume(this._change, cancellationToken);
            var message = $"{Strings.Toast_Volume}: {VolumePresentation.FormatLevel(state.VolumePercent)}";
            return Task.FromResult(this._resultFactory.Create($"{(state.IsMuted ? "🔇" : "🔊")} {message}"));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }

        return Task.FromResult(this._resultFactory.Create(Strings.Toast_CantChangeVolume!));
    }
}
