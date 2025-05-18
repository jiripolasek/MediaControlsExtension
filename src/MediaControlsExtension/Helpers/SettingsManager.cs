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

    public bool ShowThumbnails => this._showThumbnailsOption.Value;

    public SettingsManager()
    {
        this.FilePath = SettingsJsonPath();
        this.Settings.Add(this._showThumbnailsOption);
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