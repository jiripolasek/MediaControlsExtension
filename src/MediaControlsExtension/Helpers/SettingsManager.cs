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
        Strings.Settings_ShowThumbnails_Title! + Environment.NewLine + Strings.Settings_ShowThumbnails_Subtitle!,
        "",
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
        Strings.Settings_KeepOpen_Title! + Environment.NewLine + Strings.Settings_KeepOpen_Subtitle!,
        "",
        true
        );

    [SuppressMessage("Maintainability", "CA1507:Use nameof to express symbol names", Justification = "Settings key is independent to ensure its compatible")]
    private readonly ToggleSetting _keepOpenTogglePlayPauseCurrent = new(
        Namespaced("KeepOpenTogglePlayPauseCurrent"),
        Strings.Settings_HideAfterPlayPause!,
        "",
        false
    );

    [SuppressMessage("Maintainability", "CA1507:Use nameof to express symbol names", Justification = "Settings key is independent to ensure its compatible")]
    private readonly ToggleSetting _keepOpenSkipTrack = new(
        Namespaced("KeepOpenSkipTrack"),
        Strings.Settings_HideAfterChangingTracks!,
        "",
        false
    );

    [SuppressMessage("Maintainability", "CA1507:Use nameof to express symbol names", Justification = "Settings key is independent to ensure its compatible")]
    private readonly ToggleSetting _keepOpenTogglePlayMedia = new(
        Namespaced("KeepOpenTogglePlayMedia"),
        Strings.Settings_HideAfterTogglePlayPause!,
        "",
        false
    );

    [SuppressMessage("Maintainability", "CA1507:Use nameof to express symbol names", Justification = "Settings key is independent to ensure its compatible")]
    private readonly ToggleSetting _showToastMessages = new(
        Namespaced("ShowToastMessages"),
        Strings.Settings_ShowToastMessages_Title! + Environment.NewLine + Strings.Settings_ShowToastMessages_Subtitle!,
        "",
        true
    );

    [SuppressMessage("Maintainability", "CA1507:Use nameof to express symbol names", Justification = "Settings key is independent to ensure its compatible")]
    private readonly ToggleSetting _pauseOthersOnPlay = new(
        Namespaced("PauseOthersOnPlay"),
        Strings.Settings_PauseOthersOnPlay_Title! + Environment.NewLine + Strings.Settings_PauseOthersOnPlay_Subtitle!,
        "",
        true);

    [SuppressMessage("Maintainability", "CA1507:Use nameof to express symbol names", Justification = "Settings key is independent to ensure its compatible")]
    private readonly ToggleSetting _showCurrentMediaAtTopLevel = new(
        Namespaced("ShowCurrentMediaAtTopLevel"),
        Strings.Settings_ShowCurrentMediaAtTopLevel_Title! + Environment.NewLine + Strings.Settings_ShowCurrentMediaAtTopLevel_Subtitle!,
        "",
        true);

    [SuppressMessage("Maintainability", "CA1507:Use nameof to express symbol names", Justification = "Settings key is independent to ensure its compatible")]
    private readonly ToggleSetting _showSkipCommands = new(
        Namespaced("ShowSkipCommands"),
        Strings.Settings_ShowSkipCommands_Title! + Environment.NewLine + Strings.Settings_ShowSkipCommands_Subtitle!,
        "",
        true);

    [SuppressMessage("Maintainability", "CA1507:Use nameof to express symbol names", Justification = "Settings key is independent to ensure its compatible")]
    private readonly ToggleSetting _showSkipCommandsInDockBand = new(
        Namespaced("ShowSkipCommandsInDockBand"),
        Strings.Settings_ShowSkipCommandsInDock_Title! + Environment.NewLine + Strings.Settings_ShowSkipCommandsInDock_Subtitle!,
        "",
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

    public bool ShowSkipCommandsInDockBand => _showSkipCommandsInDockBand.Value;

    public SettingsManager()
    {
        this.FilePath = SettingsJsonPath();

        this.Settings.Add(this._showCurrentMediaAtTopLevel);
        this.Settings.Add(this._showSkipCommands);
        this.Settings.Add(this._showSkipCommandsInDockBand);
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
