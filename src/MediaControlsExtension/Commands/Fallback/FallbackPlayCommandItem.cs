// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class FallbackPlayCommandItem : FallbackCommandItem
{
    private readonly SettingsManager _settingsManager;
    private readonly PlayPauseMediaCommand _command;
    private readonly QueryCommandProcessor _queryProcessor = new([
        new("pl", "Play"),
        new("pa", "Pause"),
        new("t", "Toggle Play/Pause"),
        new("media", "Media Controls: Play/Pause"),
    ]);

    public FallbackPlayCommandItem(ICommand command, string displayTitle, SettingsManager settingsManager) : base(command, displayTitle, "com.jpsoftworks.cmdpal.mediacontrols.play")
    {
        this._settingsManager = settingsManager;
        this._command = (PlayPauseMediaCommand)command;
        this._command.Name = "";
        this.Title = "";
        this.Subtitle = Strings.TogglePlayPause_Comments!;
    }
    public override void UpdateQuery(string query)
    {
        this.Title = this._settingsManager.GlobalCommands == GlobalCommandsMode.Disabled
            ? this._command.Name = ""
            : this._command.Name = this._queryProcessor.ProcessQuery(query);
    }
}