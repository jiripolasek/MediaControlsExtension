// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class ToggleDetailedLoggingCommand : InvokableCommand
{
    private Action<bool>? _stateChanged;

    public ToggleDetailedLoggingCommand()
    {
        this.UpdatePresentation(DetailedLoggingMode.IsEnabled);
    }

    public override ICommandResult Invoke()
    {
        var enabled = DetailedLoggingMode.Toggle();
        this.UpdatePresentation(enabled);
        this._stateChanged?.Invoke(enabled);
        return CommandResult.KeepOpen();
    }

    internal void SetStateChangedHandler(Action<bool> stateChanged)
    {
        ArgumentNullException.ThrowIfNull(stateChanged);
        this._stateChanged = stateChanged;
    }

    private void UpdatePresentation(bool enabled)
    {
        this.Name = enabled
            ? Strings.ReportProblem_DetailedLogging_Disable_Title!
            : Strings.ReportProblem_DetailedLogging_Enable_Title!;
        this.Icon = enabled
            ? Icons.DetailedLoggingDisabled
            : Icons.DetailedLoggingEnabled;
    }
}
