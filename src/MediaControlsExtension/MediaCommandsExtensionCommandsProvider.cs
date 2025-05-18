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
using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension;

public sealed partial class MediaControlsExtensionCommandsProvider : CommandProvider
{
    private readonly SettingsManager _settingsManager = new();
    private readonly ICommandItem[] _commands;

    private IFallbackCommandItem[]? _fallbackCommands;
    private IFallbackCommandItem? _playPauseFallback;
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;

    public MediaControlsExtensionCommandsProvider()
    {
        this.Id = "JPSoftworks.CmdPal.MediaControls";
        this.DisplayName = Strings.Name!;
        this.Icon = Icons.MainIcon;
        this.Settings = this._settingsManager.Settings;

        var mediaControlsExtensionPage = new MediaControlsExtensionPage(this._settingsManager);

        this._commands =
        [
            new CommandItem(mediaControlsExtensionPage)
            {
                Title = this.DisplayName,
                Subtitle = "Manage playback and switch between media apps with ease",
                MoreCommands = [new CommandContextItem(this.Settings.SettingsPage!)]
            },
        ];

        this._fallbackCommands = [];

        _ = this.InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        this._sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync()!;

        this._playPauseFallback = new FallbackPlayCommandItem(new PlayPauseMediaCommand(this._sessionManager!), Strings.TogglePlayPause!)
        {
            Subtitle = Strings.TogglePlayPause_Comments!
        };

        this._fallbackCommands = [this._playPauseFallback];

        this.RaiseItemsChanged();
    }

    public override ICommandItem[] TopLevelCommands()
    {
        return this._commands;
    }

    public override IFallbackCommandItem[]? FallbackCommands()
    {
        return this._fallbackCommands;
    }
}