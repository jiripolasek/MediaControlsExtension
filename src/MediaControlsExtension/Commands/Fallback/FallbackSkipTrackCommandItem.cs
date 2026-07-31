// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class FallbackSkipTrackCommandItem : FallbackCommandItem, IIconThemeAware
{
    private readonly SettingsManager _settingsManager;
    private readonly NextTrackInvokableMediaCommand _command;
    private readonly IIconService _iconService;
    private readonly QueryCommandProcessor _queryProcessor = new((CommandMapping[])[
        new("skip", "Skip track"),
        new("next", "Next track"),
        new("play n", "Play next track"),
        new("media", "Media Controls: Next track"),
    ]);

    public FallbackSkipTrackCommandItem(
        IMediaService mediaService,
        Task initialization,
        SettingsManager settingsManager,
        MediaCommandResultFactory resultFactory,
        IIconService iconService,
        ILoggerFactory loggerFactory)
        : base(new NoOpCommand(), Strings.Command_NextTrack, "com.jpsoftworks.cmdpal.mediacontrols.next")
    {
        this._settingsManager = settingsManager;
        this._iconService = iconService;
        this.Command = this._command = new(mediaService, initialization, resultFactory, iconService, loggerFactory) { Name = "" };
        this.Title = "";
    }

    public void RefreshIconTheme()
        => this._command.UpdateIcon(this._iconService.GetIcon(
            ThemedIcon.SkipNext,
            IconSurface.CommandPalette));

    public override void UpdateQuery(string query)
    {
        this.Title = this._settingsManager.GlobalCommands == GlobalCommandsMode.Disabled
            ? this._command.Name = ""
            : this._command.Name = this._queryProcessor.ProcessQuery(query);
    }
}
