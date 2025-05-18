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

internal sealed partial class ToggleMuteMediaInvokableCommand : AsyncInvokableCommand
{
    public override IconInfo Icon => Icons.ToggleMute;
    public override string Name => Strings.Command_ToggleMute!;

    protected override async Task<ICommandResult> InvokeAsync()
    {
        try
        {
            using CoreAudioController coreAudioController = new();
            var playbackDevice = coreAudioController.GetDefaultDevice(DeviceType.Playback, Role.Console);
            if (playbackDevice != null)
            {
                await playbackDevice.ToggleMuteAsync()!.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }

        return CommandResult.Dismiss();
    }
}