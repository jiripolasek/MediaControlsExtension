// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class ToggleMuteMediaInvokableCommand : AsyncInvokableCommand
{
    private readonly SystemVolumeService _systemVolumeService;
    private readonly YetAnotherHelper _yetAnotherHelper;
    public override string Name => Strings.Command_ToggleMute!;

    public ToggleMuteMediaInvokableCommand(SystemVolumeService systemVolumeService, YetAnotherHelper yetAnotherHelper)
    {
        this._systemVolumeService = systemVolumeService;
        this._yetAnotherHelper = yetAnotherHelper;
        this.Icon = Icons.ToggleMute;
    }

    protected override Task<ICommandResult> InvokeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var state = this._systemVolumeService.ToggleMute(cancellationToken);
            return Task.FromResult(this._yetAnotherHelper.GetMediaCommandResult(state.IsMuted ? $"🔇 {Strings.Toast_Muted}" : $"🔊 {Strings.Toast_Unmuted}"));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }

        return Task.FromResult(this._yetAnotherHelper.GetMediaCommandResult(Strings.Toast_CantChangeVolume!));
    }
}
