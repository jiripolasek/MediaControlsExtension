// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class FallbackUnmuteCommandItem : FallbackCommandItem
{
    private readonly SettingsManager _settingsManager;
    private readonly SetMuteMediaInvokableCommand _command;
    private readonly QueryCommandProcessor _queryProcessor = new([
        new("un", "Unmute"),
        new("media", "Media Controls: Unmute"),
        new("vol", "Volume unmute"),
    ]);

    public FallbackUnmuteCommandItem(
        SettingsManager settingsManager,
        SystemVolumeService systemVolumeService,
        YetAnotherHelper yetAnotherHelper)
        : base(new NoOpCommand(), Strings.Command_Unmute, "com.jpsoftworks.cmdpal.mediacontrols.unmute")
    {
        this._settingsManager = settingsManager;
        this.Command = this._command = new(false, systemVolumeService, yetAnotherHelper) { Name = "" };
        this.Title = "";
        this.Subtitle = Strings.Command_Unmute_Subtitle!;
    }

    public override void UpdateQuery(string query)
    {
        this.Title = !this._settingsManager.EnableVolumeControls ||
                     this._settingsManager.GlobalCommands == GlobalCommandsMode.Disabled
            ? this._command.Name = ""
            : this._command.Name = this._queryProcessor.ProcessQuery(query);
    }
}
