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
    private readonly MediaCommandResultFactory _resultFactory;

    public SetVolumeMediaInvokableCommand(
        int volumePercent,
        SystemVolumeService systemVolumeService,
        MediaCommandResultFactory resultFactory,
        ILoggerFactory loggerFactory)
        : base(loggerFactory)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(volumePercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(volumePercent, 100);

        this._volumePercent = volumePercent;
        this._systemVolumeService = systemVolumeService;
        this._resultFactory = resultFactory;
        this.Name = VolumePresentation.FormatSetVolumeName(volumePercent);
        this.Icon = VolumePresentation.GetIcon(volumePercent);
    }

    protected override Task<ICommandResult> InvokeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var state = this._systemVolumeService.SetVolume(this._volumePercent, cancellationToken);
            var message = $"{Strings.Toast_Volume}: {VolumePresentation.FormatLevel(state.VolumePercent)}";
            return Task.FromResult(this._resultFactory.Create($"{(state.IsMuted ? "🔇" : "🔊")} {message}"));
        }
        catch (Exception ex)
        {
            ExtensionLog.UnexpectedError(this.Logger, ex);
        }

        return Task.FromResult(this._resultFactory.Create(Strings.Toast_CantChangeVolume!));
    }
}
