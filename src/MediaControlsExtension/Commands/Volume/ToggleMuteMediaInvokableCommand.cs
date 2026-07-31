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
    private readonly string _name;

    public override string Name => this._name;

    public ToggleMuteMediaInvokableCommand(
        SystemVolumeService systemVolumeService,
        MediaCommandResultFactory resultFactory,
        ILoggerFactory loggerFactory)
        : this(
            systemVolumeService,
            resultFactory,
            loggerFactory,
            Strings.Command_ToggleMute!)
    {
    }

    internal ToggleMuteMediaInvokableCommand(
        SystemVolumeService systemVolumeService,
        MediaCommandResultFactory resultFactory,
        ILoggerFactory loggerFactory,
        string commandName)
        : base(loggerFactory)
    {
        this._systemVolumeService = systemVolumeService;
        this._resultFactory = resultFactory;
        this._name = commandName;
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
            ExtensionLog.UnexpectedError(this.Logger, ex);
        }

        return Task.FromResult(this._resultFactory.Create(Strings.Toast_CantChangeVolume!));
    }
}
