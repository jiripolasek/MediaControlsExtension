// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class SetVolumeMediaInvokableCommand : AsyncInvokableCommand
{
    private readonly int _volumePercent;
    private readonly SystemVolumeService _systemVolumeService;
    private readonly YetAnotherHelper _yetAnotherHelper;

    public SetVolumeMediaInvokableCommand(
        int volumePercent,
        SystemVolumeService systemVolumeService,
        YetAnotherHelper yetAnotherHelper)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(volumePercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(volumePercent, 100);

        this._volumePercent = volumePercent;
        this._systemVolumeService = systemVolumeService;
        this._yetAnotherHelper = yetAnotherHelper;
        this.Name = VolumePresentation.FormatSetVolumeName(volumePercent);
        this.Icon = VolumePresentation.GetIcon(volumePercent);
    }

    protected override Task<ICommandResult> InvokeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var state = this._systemVolumeService.SetVolume(this._volumePercent, cancellationToken);
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
