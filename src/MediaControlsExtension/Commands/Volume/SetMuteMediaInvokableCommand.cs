// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class SetMuteMediaInvokableCommand : AsyncInvokableCommand
{
    private readonly bool _targetMute;
    private readonly SystemVolumeService _systemVolumeService;
    private readonly YetAnotherHelper _yetAnotherHelper;

    public SetMuteMediaInvokableCommand(
        bool targetMute,
        SystemVolumeService systemVolumeService,
        YetAnotherHelper yetAnotherHelper,
        IIconService iconService)
    {
        this._targetMute = targetMute;
        this._systemVolumeService = systemVolumeService;
        this._yetAnotherHelper = yetAnotherHelper;
        this.Name = targetMute ? Strings.Command_Mute! : Strings.Command_Unmute!;
        this.Icon = iconService.GetIcon(
            targetMute ? ThemedIcon.VolumeMute : ThemedIcon.VolumeOff,
            IconSurface.CommandPalette);
    }

    protected override Task<ICommandResult> InvokeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var state = this._systemVolumeService.SetMute(this._targetMute, cancellationToken);
            return Task.FromResult(this._yetAnotherHelper.GetMediaCommandResult(state.IsMuted ? $"🔇 {Strings.Toast_Muted}" : $"🔊 {Strings.Toast_Unmuted}"));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }

        return Task.FromResult(this._yetAnotherHelper.GetMediaCommandResult(Strings.Toast_CantChangeVolume!));
    }
}