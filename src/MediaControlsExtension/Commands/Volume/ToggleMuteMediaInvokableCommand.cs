// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using AudioSwitcher.AudioApi;
using AudioSwitcher.AudioApi.CoreAudio;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class ToggleMuteMediaInvokableCommand : AsyncInvokableCommand
{
    private readonly YetAnotherHelper _yetAnotherHelper;
    public override IconInfo Icon => Icons.ToggleMute;
    public override string Name => Strings.Command_ToggleMute!;

    public ToggleMuteMediaInvokableCommand(YetAnotherHelper yetAnotherHelper)
    {
        this._yetAnotherHelper = yetAnotherHelper;
    }

    protected override async Task<ICommandResult> InvokeAsync()
    {
        try
        {
            using CoreAudioController coreAudioController = new();
            var playbackDevice = coreAudioController.GetDefaultDevice(DeviceType.Playback, Role.Console);
            if (playbackDevice != null)
            {
                var isMuted = playbackDevice.IsMuted;
                await playbackDevice.ToggleMuteAsync()!.ConfigureAwait(false);

                return this._yetAnotherHelper.GetMediaCommandResult(isMuted ? "🔊 Unmuted" : "🔇 Muted");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }

        return this._yetAnotherHelper.GetMediaCommandResult("Can't change the volume");
    }
}