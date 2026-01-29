// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension;

public sealed partial class MediaControlsExtensionCommandsProvider : CommandProvider
{
    private readonly YetAnotherHelper _yetAnotherHelper;
    private readonly MediaService _mediaService = new();
    private readonly SettingsManager _settingsManager = new();
    private readonly CommandItem _mediaControlsPageItem;
    private readonly CommandItem _nowPlayingItem;
    private ICommandItem[] _commands = [];
    private IFallbackCommandItem[]? _fallbackCommands = [];
    private readonly ICommandItem[] _bands;

    public MediaControlsExtensionCommandsProvider()
    {
        this.Id = "JPSoftworks.CmdPal.MediaControls";
        this.DisplayName = Strings.Name!;
        this.Icon = Icons.MainIcon;
        this.Settings = this._settingsManager.Settings;

        this._settingsManager.Settings.SettingsChanged += this.SettingsOnSettingsChanged;
        this._yetAnotherHelper = new(this._settingsManager);

        MediaSessionOperations.Initialize(this._settingsManager);

        var mediaControlsExtensionPage = new MediaControlsExtensionPage(this._mediaService, this._settingsManager, this._yetAnotherHelper);
        this._mediaControlsPageItem = new(mediaControlsExtensionPage) { Title = this.DisplayName, Subtitle = Strings.MediaControls_Subtitle!, MoreCommands = [new CommandContextItem(this.Settings.SettingsPage!)] };
        this._nowPlayingItem = new NowPlayingListItem(this._mediaService, this._settingsManager, this._yetAnotherHelper, false);
        var mediaControlsBand = new MediaControlsExtensionPage(this._mediaService, this._settingsManager, this._yetAnotherHelper, true);
        this._bands = [new CommandItem(mediaControlsBand) { Title = Strings.Name! }];
        this.UpdateTopLevelCommands();

        _ = Task.Run(this.InitializeMediaServiceSafe);
        _ = Task.Run(this.InitializeFallbackCommands);
    }

    private async Task InitializeMediaServiceSafe()
    {
        try
        {
            await this._mediaService.InitializeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }
    }

    private Task? InitializeFallbackCommands()
    {
        try
        {
            var sessionManagerTask = GlobalSystemMediaTransportControlsSessionManager.RequestAsync()!;
            this._fallbackCommands = [
                new FallbackPlayCommandItem(new PlayPauseMediaCommand(sessionManagerTask!, this._settingsManager, this._yetAnotherHelper), Strings.TogglePlayPause!, this._settingsManager),
                new FallbackUnmuteCommandItem(this._settingsManager, this._yetAnotherHelper),
                new FallbackMuteCommandItem(this._settingsManager, this._yetAnotherHelper),
                new FallbackSkipTrackCommandItem(sessionManagerTask, this._settingsManager, this._yetAnotherHelper),
                new FallbackPreviousTrackCommandItem(sessionManagerTask, this._settingsManager, this._yetAnotherHelper)
            ];
            this.RaiseItemsChanged();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }

        return Task.CompletedTask;
    }

    private void SettingsOnSettingsChanged(object sender, Settings args)
    {
        this.UpdateTopLevelCommands();
    }

    private void UpdateTopLevelCommands()
    {
        this._commands = this._settingsManager.ShowCurrentMediaAtTopLevel ? [this._mediaControlsPageItem, this._nowPlayingItem] : [this._mediaControlsPageItem];
        this.RaiseItemsChanged();
    }

    public override ICommandItem[] TopLevelCommands() => this._commands;

    public override IFallbackCommandItem[]? FallbackCommands() => this._fallbackCommands;

    public override ICommandItem[]? GetDockBands()
    {
        return _bands;
    }
}
