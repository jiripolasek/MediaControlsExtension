// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class ToggleMuteMediaInvokableCommand : AsyncInvokableCommand
{
    private readonly SystemVolumeService _systemVolumeService;
    private readonly MediaCommandResultFactory _resultFactory;
    public override string Name => Strings.Command_ToggleMute!;

    public ToggleMuteMediaInvokableCommand(SystemVolumeService systemVolumeService, MediaCommandResultFactory resultFactory)
    {
        this._systemVolumeService = systemVolumeService;
        this._resultFactory = resultFactory;
        this.Icon = Icons.ToggleMute;
    }

    protected override Task<ICommandResult> InvokeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var state = this._systemVolumeService.ToggleMute(cancellationToken);
            return Task.FromResult(this._resultFactory.Create(state.IsMuted ? $"🔇 {Strings.Toast_Muted}" : $"🔊 {Strings.Toast_Unmuted}"));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }

        return Task.FromResult(this._resultFactory.Create(Strings.Toast_CantChangeVolume!));
    }
}
