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
    private readonly MediaCommandResultFactory _resultFactory;

    public SetMuteMediaInvokableCommand(
        bool targetMute,
        SystemVolumeService systemVolumeService,
        MediaCommandResultFactory resultFactory,
        IIconService iconService,
        ILoggerFactory loggerFactory)
        : base(loggerFactory)
    {
        this._targetMute = targetMute;
        this._systemVolumeService = systemVolumeService;
        this._resultFactory = resultFactory;
        this.Name = targetMute ? Strings.Command_Mute! : Strings.Command_Unmute!;
        this.Icon = iconService.GetIcon(
            targetMute ? ThemedIcon.VolumeMute : ThemedIcon.VolumeUnmute,
            IconSurface.CommandPalette);
    }

    protected override Task<ICommandResult> InvokeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var state = this._systemVolumeService.SetMute(this._targetMute, cancellationToken);
            return Task.FromResult(this._resultFactory.Create(state.IsMuted ? $"🔇 {Strings.Toast_Muted}" : $"🔊 {Strings.Toast_Unmuted}"));
        }
        catch (Exception ex)
        {
            ExtensionLog.UnexpectedError(this.Logger, ex);
        }

        return Task.FromResult(this._resultFactory.Create(Strings.Toast_CantChangeVolume!));
    }
}
