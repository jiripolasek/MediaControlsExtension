// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class FallbackPlayCommandItem : FallbackCommandItem, IIconThemeAware
{
    private readonly SettingsManager _settingsManager;
    private readonly PlayPauseMediaCommand _command;
    private readonly IIconService _iconService;
    private readonly QueryCommandProcessor _queryProcessor = new((CommandMapping[])[
        new("pl", "Play"),
        new("pa", "Pause"),
        new("t", "Toggle Play/Pause"),
        new("media", "Media Controls: Play/Pause"),
    ]);

    public FallbackPlayCommandItem(
        ICommand command,
        string displayTitle,
        SettingsManager settingsManager,
        IIconService iconService) : base(command, displayTitle, "com.jpsoftworks.cmdpal.mediacontrols.play")
    {
        this._settingsManager = settingsManager;
        this._command = (PlayPauseMediaCommand)command;
        this._iconService = iconService;
        this._command.Name = "";
        this.Title = "";
    }

    public void RefreshIconTheme()
        => this._command.UpdateIcon(this._iconService.GetIcon(
            ThemedIcon.PlayPause,
            IconSurface.CommandPalette));

    public override void UpdateQuery(string query)
    {
        this.Title = this._settingsManager.GlobalCommands == GlobalCommandsMode.Disabled
            ? this._command.Name = ""
            : this._command.Name = this._queryProcessor.ProcessQuery(query);
    }
}
