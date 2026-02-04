// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Foundation;
using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class FallbackSkipTrackCommandItem : FallbackCommandItem
{
    private readonly SettingsManager _settingsManager;
    private readonly NextTrackInvokableMediaCommand _command;
    private readonly QueryCommandProcessor _queryProcessor = new([
        new("skip", "Skip track"),
        new("next", "Next track"),
        new("play n", "Play next track"),
        new("media", "Media Controls: Next track"),
    ]);

    public FallbackSkipTrackCommandItem(
        IAsyncOperation<GlobalSystemMediaTransportControlsSessionManager> getSessionManagerOperation,
        SettingsManager settingsManager,
        YetAnotherHelper yetAnotherHelper)
        : base(new NoOpCommand(), Strings.Command_NextTrack, "com.jpsoftworks.cmdpal.mediacontrols.next")
    {
        this._settingsManager = settingsManager;
        this.Command = this._command = new(getSessionManagerOperation, yetAnotherHelper) { Name = "" };
        this.Title = "";
        this.Subtitle = Strings.Command_NextTrack_Subtitle!;
    }

    public override void UpdateQuery(string query)
    {
        this.Title = this._settingsManager.GlobalCommands == GlobalCommandsMode.Disabled
            ? this._command.Name = ""
            : this._command.Name = this._queryProcessor.ProcessQuery(query);
    }
}