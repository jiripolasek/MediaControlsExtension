// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class FallbackUnmuteCommandItem : FallbackCommandItem, IIconThemeAware
{
    private readonly SettingsManager _settingsManager;
    private readonly SetMuteMediaInvokableCommand _command;
    private readonly IIconService _iconService;
    private readonly QueryCommandProcessor _queryProcessor = new([
        new("un", "Unmute"),
        new("media", "Media Controls: Unmute"),
        new("vol", "Volume unmute"),
    ]);

    public FallbackUnmuteCommandItem(
        SettingsManager settingsManager,
        SystemVolumeService systemVolumeService,
        MediaCommandResultFactory resultFactory,
        IIconService iconService)
        : base(new NoOpCommand(), Strings.Command_Unmute, "com.jpsoftworks.cmdpal.mediacontrols.unmute")
    {
        this._settingsManager = settingsManager;
        this._iconService = iconService;
        this.Command = this._command = new(false, systemVolumeService, resultFactory, iconService) { Name = "" };
        this.Title = "";
    }

    public void RefreshIconTheme()
        => this._command.UpdateIcon(this._iconService.GetIcon(
            ThemedIcon.VolumeOff,
            IconSurface.CommandPalette));

    public override void UpdateQuery(string query)
    {
        this.Title = !this._settingsManager.EnableVolumeControls ||
                     this._settingsManager.GlobalCommands == GlobalCommandsMode.Disabled
            ? this._command.Name = ""
            : this._command.Name = this._queryProcessor.ProcessQuery(query);
    }
}
