// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Foundation;
using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class FallbackPreviousTrackCommandItem : FallbackCommandItem
{
    private readonly SettingsManager _settingsManager;
    private readonly PreviousTrackInvokableMediaCommand _command;
    private readonly QueryCommandProcessor _queryProcessor = new([
        new("pre", "Previous track"),
        new("media", "Media Controls: Previous track"),
    ]);

    public FallbackPreviousTrackCommandItem(
        IAsyncOperation<GlobalSystemMediaTransportControlsSessionManager> getSessionManagerOperation,
        SettingsManager settingsManager,
        YetAnotherHelper yetAnotherHelper)
        : base(new NoOpCommand(), Strings.Command_PreviousTrack, "com.jpsoftworks.cmdpal.mediacontrols.previous")
    //: base(new NoOpCommand(), Strings.Command_PreviousTrack)
    {
        this._settingsManager = settingsManager;
        this.Command = this._command = new(getSessionManagerOperation, yetAnotherHelper) { Name = "" };
        this.Title = "";
        this.Subtitle = Strings.Command_PreviousTrack_Subtitle!;
    }

    public override void UpdateQuery(string query)
    {
        this.Title = this._settingsManager.GlobalCommands == GlobalCommandsMode.Disabled
            ? this._command.Name = ""
            : this._command.Name = this._queryProcessor.ProcessQuery(query);
    }
}