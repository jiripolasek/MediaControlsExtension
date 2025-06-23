// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Commands;
using JPSoftworks.MediaControlsExtension.Pages;
using JPSoftworks.MediaControlsExtension.Resources;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;
using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension;

public sealed partial class MediaControlsExtensionCommandsProvider : CommandProvider
{
    private readonly SettingsManager _settingsManager = new();
    private readonly ICommandItem[] _commands;
    private readonly IFallbackCommandItem[]? _fallbackCommands;
    private readonly IAsyncOperation<GlobalSystemMediaTransportControlsSessionManager> _sessionManagerTask;

    public MediaControlsExtensionCommandsProvider()
    {
        this.Id = "JPSoftworks.CmdPal.MediaControls";
        this.DisplayName = Strings.Name!;
        this.Icon = Icons.MainIcon;
        this.Settings = this._settingsManager.Settings;

        var mediaControlsExtensionPage = new MediaControlsExtensionPage(this._settingsManager);
        this._sessionManagerTask = GlobalSystemMediaTransportControlsSessionManager.RequestAsync()!;

        this._commands =
        [
            new CommandItem(mediaControlsExtensionPage)
            {
                Title = this.DisplayName,
                Subtitle = Strings.MediaControls_Subtitle!,
                MoreCommands = [new CommandContextItem(this.Settings.SettingsPage!)]
            },
        ];

        this._fallbackCommands = [
            new FallbackPlayCommandItem(new PlayPauseMediaCommand(this._sessionManagerTask!), Strings.TogglePlayPause!, this._settingsManager),
            new FallbackUnmuteCommandItem(this._settingsManager),
            new FallbackMuteCommandItem(this._settingsManager),
            new FallbackSkipTrackCommandItem(this._sessionManagerTask, this._settingsManager),
            new FallbackPreviousTrackCommandItem(this._sessionManagerTask, this._settingsManager)
        ];
    }

    public override ICommandItem[] TopLevelCommands() => this._commands;

    public override IFallbackCommandItem[]? FallbackCommands() => this._fallbackCommands;
}