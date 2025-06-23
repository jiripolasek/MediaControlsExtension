// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Resources;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal sealed class SettingsManager : JsonSettingsManager
{
    private const string DefaultNamespace = "jpsoftworks.mediacontrols";

    private readonly ToggleSetting _showThumbnailsOption = new(
        Namespaced(nameof(ShowThumbnails)),
        Strings.Settings_ShowThumbnails_Title!,
        Strings.Settings_ShowThumbnails_Subtitle!,
        false);

    private readonly ChoiceSetSetting _globalCommands = new(
        Namespaced(nameof(GlobalCommands)),
        Strings.Settings_GlobalCommands_Title!,
        Strings.Settings_GlobalCommands_Subtitle!,
        [
            // first option is the default one (hardcoded in the extension SDK)
            new ChoiceSetSetting.Choice(Strings.Settings_GlobalCommands_Option_Enabled!, GlobalCommandsMode.Enabled.ToString("G")),
            new ChoiceSetSetting.Choice(Strings.Settings_GlobalCommands_Option_Disabled!, GlobalCommandsMode.Disabled.ToString("G")),
            // new ChoiceSetSetting.Choice("With slash ('/') prefix", GlobalCommandsMode.SlashPrefix.ToString("G")),
        ]);

    public bool ShowThumbnails => this._showThumbnailsOption.Value;

    public GlobalCommandsMode GlobalCommands =>
        string.IsNullOrWhiteSpace(this._globalCommands.Value)
            ? GlobalCommandsMode.Enabled
            : Enum.TryParse(this._globalCommands.Value, true, out GlobalCommandsMode result)
                ? result
                : GlobalCommandsMode.Enabled;

    public SettingsManager()
    {
        this.FilePath = SettingsJsonPath();
        this.Settings.Add(this._showThumbnailsOption);
        this.Settings.Add(this._globalCommands);
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