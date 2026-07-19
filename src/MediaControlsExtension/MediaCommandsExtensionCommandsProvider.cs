// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension;

public sealed partial class MediaControlsExtensionCommandsProvider : CommandProvider, IDisposable
{
    private readonly YetAnotherHelper _yetAnotherHelper;
    private readonly MediaService _mediaService = new();
    private readonly SystemVolumeService _systemVolumeService = new();
    private readonly SettingsManager _settingsManager = new();
    private readonly CommandItem _mediaControlsPageItem;
    private readonly CommandItem _nowPlayingItem;
    private readonly MediaControlsExtensionPage _mediaControlsExtensionPage;
    private readonly MediaControlsExtensionPage _mediaControlsBand;
    private readonly ToggleMuteCommandItem _toggleMuteCommandItem;
    private readonly ICommandItem[] _volumeCommands;
    private ICommandItem[] _commands = [];
    private IFallbackCommandItem[]? _fallbackCommands = [];
    private IFallbackCommandItem[] _fallbackCommandsWithVolume = [];
    private IFallbackCommandItem[] _fallbackCommandsWithoutVolume = [];
    private readonly ICommandItem[] _bands;
    private int _disposeState;

    public MediaControlsExtensionCommandsProvider()
    {
        this.Id = "JPSoftworks.CmdPal.MediaControls";
        this.DisplayName = Strings.Name!;
        this.Icon = Icons.MainIcon;
        this.Settings = this._settingsManager.Settings;

        this._settingsManager.Settings.SettingsChanged += this.SettingsOnSettingsChanged;
        this._yetAnotherHelper = new(this._settingsManager);

        this._mediaControlsExtensionPage = new(this._mediaService, this._systemVolumeService, this._settingsManager, this._yetAnotherHelper);
        this._mediaControlsPageItem = new(this._mediaControlsExtensionPage) { Title = this.DisplayName, Subtitle = Strings.MediaControls_Subtitle!, MoreCommands = [new CommandContextItem(this.Settings.SettingsPage!)] };
        this._nowPlayingItem = new NowPlayingListItem(this._mediaService, this._settingsManager, this._yetAnotherHelper, false);
        this._toggleMuteCommandItem = new(this._systemVolumeService, this._yetAnotherHelper)
        {
            Title = Strings.Command_ToggleMute!,
        };
        this._volumeCommands =
        [
            new CommandItem(new ChangeVolumeMediaInvokableCommand(VolumeChange.Increase, this._systemVolumeService, this._yetAnotherHelper))
            {
                Title = Strings.Command_VolumeUp!,
                Icon = Icons.Volume_Max,
            },
            new CommandItem(new ChangeVolumeMediaInvokableCommand(VolumeChange.Decrease, this._systemVolumeService, this._yetAnotherHelper))
            {
                Title = Strings.Command_VolumeDown!,
                Icon = Icons.Volume_Low,
            },
            this._toggleMuteCommandItem,
            .. VolumeCommandFactory.CreatePresetCommandItems(this._systemVolumeService, this._yetAnotherHelper),
        ];
        this._mediaControlsBand = new(this._mediaService, this._systemVolumeService, this._settingsManager, this._yetAnotherHelper, true);
        this._bands = [new CommandItem(this._mediaControlsBand) { Title = Strings.Name! }];
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
            var sessionManagerTask = GsmtcOperationGate.RunAsync(
                static async _ => await GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
            var play = new FallbackPlayCommandItem(new PlayPauseMediaCommand(sessionManagerTask!, this._settingsManager, this._yetAnotherHelper), Strings.TogglePlayPause!, this._settingsManager);
            var skipNext = new FallbackSkipTrackCommandItem(sessionManagerTask, this._settingsManager, this._yetAnotherHelper);
            var skipPrevious = new FallbackPreviousTrackCommandItem(sessionManagerTask, this._settingsManager, this._yetAnotherHelper);
            this._fallbackCommandsWithoutVolume = [play, skipNext, skipPrevious];
            this._fallbackCommandsWithVolume =
            [
                play,
                new FallbackUnmuteCommandItem(this._settingsManager, this._systemVolumeService, this._yetAnotherHelper),
                new FallbackMuteCommandItem(this._settingsManager, this._systemVolumeService, this._yetAnotherHelper),
                skipNext,
                skipPrevious,
            ];
            this.UpdateFallbackCommands();
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
        this.UpdateFallbackCommands();
        this.RaiseItemsChanged();
    }

    private void UpdateTopLevelCommands()
    {
        this._commands = (this._settingsManager.ShowCurrentMediaAtTopLevel, this._settingsManager.EnableVolumeControls) switch
        {
            (true, true) => [this._mediaControlsPageItem, this._nowPlayingItem, .. this._volumeCommands],
            (true, false) => [this._mediaControlsPageItem, this._nowPlayingItem],
            (false, true) => [this._mediaControlsPageItem, .. this._volumeCommands],
            (false, false) => [this._mediaControlsPageItem],
        };
    }

    private void UpdateFallbackCommands()
        => this._fallbackCommands = this._settingsManager.EnableVolumeControls
            ? this._fallbackCommandsWithVolume
            : this._fallbackCommandsWithoutVolume;

    public override ICommandItem[] TopLevelCommands() => this._commands;

    public override IFallbackCommandItem[]? FallbackCommands() => this._fallbackCommands;

    public override ICommandItem? GetCommandItem(string id)
    {
        return this._settingsManager.EnableVolumeControls &&
               string.Equals(id, VolumeListItem.CommandId, StringComparison.Ordinal)
            ? this._mediaControlsExtensionPage.VolumeItem
            : null;
    }

    public override ICommandItem[]? GetDockBands()
    {
        return _bands;
    }

    public override void Dispose()
    {
        try
        {
            if (Interlocked.Exchange(ref this._disposeState, 1) != 0)
            {
                return;
            }

            this._settingsManager.Settings.SettingsChanged -= this.SettingsOnSettingsChanged;
            this._toggleMuteCommandItem.Dispose();
            this._mediaControlsExtensionPage.Dispose();
            this._mediaControlsBand.Dispose();
            ((IDisposable)this._nowPlayingItem).Dispose();
            this._mediaService.Dispose();
            this._systemVolumeService.Dispose();
        }
        finally
        {
            base.Dispose();
        }
    }
}
