// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using AudioSwitcher.AudioApi;
using AudioSwitcher.AudioApi.CoreAudio;
using JPSoftworks.MediaControlsExtension.Resources;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class SetMuteMediaInvokableCommand : AsyncInvokableCommand
{
    private readonly bool _targetMute;

    public SetMuteMediaInvokableCommand(bool targetMute)
    {
        this._targetMute = targetMute;
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
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }

        return CommandResult.Dismiss();
    }
}