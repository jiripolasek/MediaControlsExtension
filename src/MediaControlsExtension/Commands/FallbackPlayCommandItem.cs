// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Resources;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;
using Windows.Media.Control;

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

    public FallbackPlayCommandItem(ICommand command, string displayTitle, SettingsManager settingsManager) : base(command, displayTitle)
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
        SettingsManager settingsManager)
        : base(new NoOpCommand(), string.Empty)
    {
        this._settingsManager = settingsManager;
        this.Command = this._command = new NextTrackInvokableMediaCommand(getSessionManagerOperation) { Name = "" };
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
        SettingsManager settingsManager)
        : base(new NoOpCommand(), string.Empty)
    {
        this._settingsManager = settingsManager;
        this.Command = this._command = new PreviousTrackInvokableMediaCommand(getSessionManagerOperation) { Name = "" };
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

internal sealed partial class FallbackMuteCommandItem : FallbackCommandItem
{
    private readonly SettingsManager _settingsManager;
    private readonly SetMuteMediaInvokableCommand _command;
    private readonly QueryCommandProcessor _queryProcessor = new([
        new("mu", "Mute"),
        new("media", "Media Controls: Mute"),
        new("vol", "Volume Mute"),
    ]);

    public FallbackMuteCommandItem(SettingsManager settingsManager) : base(new NoOpCommand(), "")
    {
        this._settingsManager = settingsManager;
        this.Command = this._command = new SetMuteMediaInvokableCommand(true) { Name = "" };
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

internal sealed partial class FallbackUnmuteCommandItem : FallbackCommandItem
{
    private readonly SettingsManager _settingsManager;
    private readonly SetMuteMediaInvokableCommand _command;
    private readonly QueryCommandProcessor _queryProcessor = new([
        new("un", "Unmute"),
        new("media", "Media Controls: Unmute"),
        new("vol", "Volume Unmute"),
    ]);

    public FallbackUnmuteCommandItem(SettingsManager settingsManager) : base(new NoOpCommand(), "")
    {
        this._settingsManager = settingsManager;
        this.Command = this._command = new SetMuteMediaInvokableCommand(false) { Name = "" };
        this.Title = "";
        this.Subtitle = Strings.Command_Unmute_Subtitle!;
    }

    public override void UpdateQuery(string query)
    {
        this.Title = this._settingsManager.GlobalCommands == GlobalCommandsMode.Disabled
            ? this._command.Name = ""
            : this._command.Name = this._queryProcessor.ProcessQuery(query);
    }
}