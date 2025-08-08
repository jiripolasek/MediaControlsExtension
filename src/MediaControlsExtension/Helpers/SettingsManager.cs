// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Diagnostics.CodeAnalysis;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal sealed class SettingsManager : JsonSettingsManager
{
    private const string DefaultNamespace = "jpsoftworks.mediacontrols";

    [SuppressMessage("Maintainability", "CA1507:Use nameof to express symbol names", Justification = "Settings key is independent to ensure its compatible")]
    private readonly ToggleSetting _showThumbnailsOption = new(
        Namespaced("ShowThumbnails"),
        "Show thumbnails\n    Use media thumbnails (album art) instead of app icons in the session list.", // Strings.Settings_ShowThumbnails_Title!,
        "", //Strings.Settings_ShowThumbnails_Subtitle!,
        false);


    [SuppressMessage("Maintainability", "CA1507:Use nameof to express symbol names", Justification = "Settings key is independent to ensure its compatible")]
    private readonly ChoiceSetSetting _globalCommands = new(
        Namespaced("GlobalCommands"),
        Strings.Settings_GlobalCommands_Title!,
        Strings.Settings_GlobalCommands_Subtitle!,
        [
            // first option is the default one (hardcoded in the extension SDK)
            new ChoiceSetSetting.Choice(Strings.Settings_GlobalCommands_Option_Enabled!, GlobalCommandsMode.Enabled.ToString("G")),
            new ChoiceSetSetting.Choice(Strings.Settings_GlobalCommands_Option_Disabled!, GlobalCommandsMode.Disabled.ToString("G")),
            // new ChoiceSetSetting.Choice("With slash ('/') prefix", GlobalCommandsMode.SlashPrefix.ToString("G")),
        ]);


    [SuppressMessage("Maintainability", "CA1507:Use nameof to express symbol names", Justification = "Settings key is independent to ensure its compatible")]
    private readonly ToggleSetting _keepOpen = new(
        Namespaced("KeepOpen"),
        "Keep Command Palette open\n    Keep the Command Palette open after running a command, so you can perform multiple actions in a row.\n    Hold Shift while activating a command to temporarily do the opposite.", //Strings.Settings_KeepOpen_Title!,
        "", //Strings.Settings_KeepOpen_Subtitle!,
        true
        );

    [SuppressMessage("Maintainability", "CA1507:Use nameof to express symbol names", Justification = "Settings key is independent to ensure its compatible")]
    private readonly ToggleSetting _keepOpenTogglePlayPauseCurrent = new(
        Namespaced("KeepOpenTogglePlayPauseCurrent"),
        "Hide after Play/Pause",
        "", //Strings.Settings_KeepOpen_Subtitle!,
        false
    );
    
    [SuppressMessage("Maintainability", "CA1507:Use nameof to express symbol names", Justification = "Settings key is independent to ensure its compatible")]
    private readonly ToggleSetting _keepOpenSkipTrack = new(
        Namespaced("KeepOpenSkipTrack"),
        "Hide after changing tracks",
        "", //Strings.Settings_KeepOpen_Subtitle!,
        false
    );

    [SuppressMessage("Maintainability", "CA1507:Use nameof to express symbol names", Justification = "Settings key is independent to ensure its compatible")]
    private readonly ToggleSetting _keepOpenTogglePlayMedia = new(
        Namespaced("KeepOpenTogglePlayMedia"),
        "Hide after toggle play/pause on media session",
        "", //Strings.Settings_KeepOpen_Subtitle!,
        false
    );

    [SuppressMessage("Maintainability", "CA1507:Use nameof to express symbol names", Justification = "Settings key is independent to ensure its compatible")]
    private readonly ToggleSetting _showToastMessages = new(
        Namespaced("ShowToastMessages"),
        "Show toast messages\n    Show a confirmation notification after each media action.",
        "", //"Show toast messages when media commands are invoked",
        true
    );

    [SuppressMessage("Maintainability", "CA1507:Use nameof to express symbol names", Justification = "Settings key is independent to ensure its compatible")]
    private readonly ToggleSetting _pauseOthersOnPlay = new(
        Namespaced("PauseOthersOnPlay"),
        "Pause other sessions on play\n    When playing a session, automatically pause all other sessions.",
        "", // ""Pauses all other media",
        true);

    [SuppressMessage("Maintainability", "CA1507:Use nameof to express symbol names", Justification = "Settings key is independent to ensure its compatible")]
    private readonly ToggleSetting _showCurrentMediaAtTopLevel = new(
        Namespaced("ShowCurrentMediaAtTopLevel"),
        "Show current media session on home page\n    Adds a “Now Playing” item to the home page for easy access to the current session.",
        "", //"Shows the current media at the top level of the command palette",
        true);

    [SuppressMessage("Maintainability", "CA1507:Use nameof to express symbol names", Justification = "Settings key is independent to ensure its compatible")]
    private readonly ToggleSetting _showSkipCommands = new(
        Namespaced("ShowSkipCommands"),
        "Show skip track commands\n    Display “Next” and “Previous Track” as top-level items on the Media Controls page.",
        "", //"Shows skip commands on the media controls page",
        true);

    public bool ShowThumbnails => this._showThumbnailsOption.Value;

    public GlobalCommandsMode GlobalCommands =>
        string.IsNullOrWhiteSpace(this._globalCommands.Value)
            ? GlobalCommandsMode.Enabled
            : Enum.TryParse(this._globalCommands.Value, true, out GlobalCommandsMode result)
                ? result
                : GlobalCommandsMode.Enabled;

    public bool KeepOpen => this._keepOpen.Value;

    public bool KeepOpenTogglePlayPauseCurrent => this._keepOpenTogglePlayPauseCurrent.Value;

    public bool KeepOpenSkipTrack => this._keepOpenSkipTrack.Value;

    public bool KeepOpenTogglePlayMedia => this._keepOpenTogglePlayMedia.Value;

    public bool ShowToastMessages => this._showToastMessages.Value;

    public bool PauseOthersOnPlay => this._pauseOthersOnPlay.Value;

    public bool ShowCurrentMediaAtTopLevel => _showCurrentMediaAtTopLevel.Value;

    public bool ShowSkipCommands => _showSkipCommands.Value;

    public SettingsManager()
    {
        this.FilePath = SettingsJsonPath();

        this.Settings.Add(this._showCurrentMediaAtTopLevel);
        this.Settings.Add(this._showSkipCommands);
        this.Settings.Add(this._showThumbnailsOption);
        this.Settings.Add(this._keepOpen);
        this.Settings.Add(this._pauseOthersOnPlay);
        this.Settings.Add(this._showToastMessages);
        this.Settings.Add(this._globalCommands);

        //this.Settings.Add(this._keepOpenTogglePlayPauseCurrent);
        //this.Settings.Add(this._keepOpenSkipTrack);
        //this.Settings.Add(this._keepOpenTogglePlayMedia);
        


        this.LoadSettings();
        this.Settings.SettingsChanged += (_, _) => this.SaveSettings();
    }

    private static string Namespaced(string propertyName)
    {
        return $"{DefaultNamespace}.{propertyName}";
    }

    private static string SettingsJsonPath()
    {
        var directory = Utilities.BaseSettingsPath("Microsoft.CmdPal");
        Directory.CreateDirectory(directory);

        // now, the state is just next to the exe
        return Path.Combine(directory, "settings.json");
    }
}

internal enum GlobalCommandsMode
{
    Disabled = 0,
    SlashPrefix = 1,
    Enabled = 2
}
