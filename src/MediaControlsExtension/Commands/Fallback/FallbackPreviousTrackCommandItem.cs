// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class FallbackPreviousTrackCommandItem : FallbackCommandItem, IIconThemeAware
{
    private readonly SettingsManager _settingsManager;
    private readonly PreviousTrackInvokableMediaCommand _command;
    private readonly IIconService _iconService;
    private readonly QueryCommandProcessor _queryProcessor = new([
        new("pre", "Previous track"),
        new("media", "Media Controls: Previous track"),
    ]);

    public FallbackPreviousTrackCommandItem(
        Task<GlobalSystemMediaTransportControlsSessionManager> getSessionManagerOperation,
        SettingsManager settingsManager,
        YetAnotherHelper yetAnotherHelper,
        IIconService iconService)
        : base(new NoOpCommand(), Strings.Command_PreviousTrack, "com.jpsoftworks.cmdpal.mediacontrols.previous")
    {
        this._settingsManager = settingsManager;
        this._iconService = iconService;
        this.Command = this._command = new(getSessionManagerOperation, yetAnotherHelper, iconService) { Name = "" };
        this.Title = "";
        this.Subtitle = Strings.Command_PreviousTrack_Subtitle!;
    }

    public void RefreshIconTheme()
        => this._command.UpdateIcon(this._iconService.GetIcon(
            ThemedIcon.SkipPrevious,
            IconSurface.CommandPalette));

    public override void UpdateQuery(string query)
    {
        this.Title = this._settingsManager.GlobalCommands == GlobalCommandsMode.Disabled
            ? this._command.Name = ""
            : this._command.Name = this._queryProcessor.ProcessQuery(query);
    }
}