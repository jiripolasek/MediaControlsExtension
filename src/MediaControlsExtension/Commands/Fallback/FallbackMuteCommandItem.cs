// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class FallbackMuteCommandItem : FallbackCommandItem
{
    private readonly SettingsManager _settingsManager;
    private readonly SetMuteMediaInvokableCommand _command;
    private readonly QueryCommandProcessor _queryProcessor = new([
        new("mu", "Mute"),
        new("media", "Media Controls: Mute"),
        new("vol", "Volume Mute"),
    ]);

    public FallbackMuteCommandItem(SettingsManager settingsManager, YetAnotherHelper yetAnotherHelper) : base(new NoOpCommand(), Strings.Command_Mute, "com.jpsoftworks.cmdpal.mediacontrols.mute")
    //public FallbackMuteCommandItem(SettingsManager settingsManager, YetAnotherHelper yetAnotherHelper) : base(new NoOpCommand(), Strings.Command_Mute)
    {
        this._settingsManager = settingsManager;
        this.Command = this._command = new(true, yetAnotherHelper) { Name = "" };
        this.Title = "";
        this.Subtitle = Strings.Command_Mute_Subtitle!;
    }
    public override void UpdateQuery(string query)
    {
        this.Title = this._settingsManager.GlobalCommands == GlobalCommandsMode.Disabled
            ? this._command.Name = ""
            : this._command.Name = this._queryProcessor.ProcessQuery(query);
    }
}