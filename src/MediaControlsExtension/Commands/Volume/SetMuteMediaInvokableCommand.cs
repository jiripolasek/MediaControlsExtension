// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using AudioSwitcher.AudioApi;
using AudioSwitcher.AudioApi.CoreAudio;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class SetMuteMediaInvokableCommand : AsyncInvokableCommand
{
    private readonly bool _targetMute;
    private readonly YetAnotherHelper _yetAnotherHelper;

    public SetMuteMediaInvokableCommand(bool targetMute, YetAnotherHelper yetAnotherHelper)
    {
        this._targetMute = targetMute;
        this._yetAnotherHelper = yetAnotherHelper;
        this.Name = targetMute ? Strings.Command_Mute! : Strings.Command_Unmute!;
        this.Icon = targetMute ? Icons.Volume_Mute : Icons.Volume_Unmute;
    }

    protected override async Task<ICommandResult> InvokeAsync()
    {
        try
        {
            using CoreAudioController coreAudioController = new();
            var playbackDevice = coreAudioController.GetDefaultDevice(DeviceType.Playback, Role.Console);
            if (playbackDevice != null)
            {
                await playbackDevice.SetMuteAsync(this._targetMute)!.ConfigureAwait(false);
                return this._yetAnotherHelper.GetMediaCommandResult(this._targetMute ? "🔇Muted" : "🔊 Unmuted");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }
        
        return this._yetAnotherHelper.GetMediaCommandResult("Can't change the volume");
    }
}