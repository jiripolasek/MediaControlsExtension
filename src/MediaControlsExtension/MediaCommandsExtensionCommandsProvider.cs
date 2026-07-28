// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension;

public sealed partial class MediaControlsExtensionCommandsProvider : CommandProvider, IDisposable
{
    private readonly MediaCommandResultFactory _resultFactory;
    private readonly MediaService _mediaService;
    private readonly MediaSessionViewModelCache _mediaSessionViewModels;
    private readonly MediaMetadataPageCache _metadataPages;
    private readonly SystemVolumeService _systemVolumeService = new();
    private readonly SettingsManager _settingsManager = new();
    private readonly IconService _iconService;
    private readonly CommandItem _mediaControlsPageItem;
    private readonly CommandItem _nowPlayingItem;
    private readonly MediaControlsExtensionPage _mediaControlsExtensionPage;
    private readonly MediaControlsExtensionPage _mediaControlsBand;
    private readonly ToggleMuteCommandItem _toggleMuteCommandItem;
    private readonly CommandItem[] _volumeCommands;
    private ICommandItem[] _commands = [];
    private IFallbackCommandItem[]? _fallbackCommands = [];
    private IFallbackCommandItem[] _fallbackCommandsWithVolume = [];
    private IFallbackCommandItem[] _fallbackCommandsWithoutVolume = [];
    private readonly ICommandItem[] _bands;
    private int _disposeState;

    public MediaControlsExtensionCommandsProvider()
    {
        this._mediaService = new MediaService(ExtensionLoggerFactory.Instance);
        this._mediaSessionViewModels = new(this._mediaService);
        this._iconService = new IconService(this._settingsManager);
        this.Id = "JPSoftworks.CmdPal.MediaControls";
        this.DisplayName = Strings.Name!;
        this.Icon = Icons.MainIcon;
        this.Settings = this._settingsManager.Settings;

        this._settingsManager.Settings.SettingsChanged += this.SettingsOnSettingsChanged;
        this._iconService.IconsChanged += this.IconServiceOnIconsChanged;
        this._resultFactory = new(this._settingsManager);
        this._metadataPages = new(
            this._mediaService,
            this._mediaSessionViewModels,
            this._resultFactory,
            this._iconService);
        this.UpdateMediaServiceOptions();

        this._mediaControlsExtensionPage = new(
            this._mediaService,
            this._mediaSessionViewModels,
            this._metadataPages,
            this._systemVolumeService,
            this._settingsManager,
            this._resultFactory,
            this._iconService);
        this._mediaControlsPageItem = new(this._mediaControlsExtensionPage)
        {
            Title = this.DisplayName,
            MoreCommands = [new CommandContextItem(this.Settings.SettingsPage!)]
        };
        IPage? currentMediaMetadataPage = null;
#if FF_ENABLE_FULL_METADATA_PAGE
        currentMediaMetadataPage = new CurrentMediaMetadataPage(
            this._mediaService,
            this._mediaSessionViewModels,
            this._resultFactory,
            this._iconService);
#endif
        this._nowPlayingItem = new NowPlayingListItem(
            this._mediaService,
            this._mediaSessionViewModels,
            this._metadataPages,
            this._settingsManager,
            this._resultFactory,
            this._iconService,
            false);
        this._toggleMuteCommandItem = new(
            this._systemVolumeService,
            this._resultFactory,
            this._iconService,
            IconSurface.CommandPalette)
        {
            Title = Strings.Command_ToggleMute!,
        };
        this._volumeCommands =
        [
            new CommandItem(new ChangeVolumeMediaInvokableCommand(VolumeChange.Increase, this._systemVolumeService, this._resultFactory))
            {
                Title = Strings.Command_VolumeUp!,
                Icon = this._iconService.GetIcon(
                    ThemedIcon.VolumeUp,
                    IconSurface.CommandPalette),
            },
            new CommandItem(new ChangeVolumeMediaInvokableCommand(VolumeChange.Decrease, this._systemVolumeService, this._resultFactory))
            {
                Title = Strings.Command_VolumeDown!,
                Icon = this._iconService.GetIcon(
                    ThemedIcon.VolumeDown,
                    IconSurface.CommandPalette),
            },
            this._toggleMuteCommandItem,
            .. VolumeCommandFactory.CreatePresetCommandItems(
                this._systemVolumeService,
                this._resultFactory,
                this._iconService,
                IconSurface.CommandPalette),
        ];
        this._mediaControlsBand = new(
            this._mediaService,
            this._mediaSessionViewModels,
            this._metadataPages,
            this._systemVolumeService,
            this._settingsManager,
            this._resultFactory,
            this._iconService,
            new DockHeadCommandTargets(
                this._mediaControlsExtensionPage,
                currentMediaMetadataPage));
        this._bands = [new CommandItem(this._mediaControlsBand) { Title = Strings.Name! }];
        this.UpdateTopLevelCommands();

        var initializationTask = Task.Run(this.InitializeMediaServiceAsync);
        _ = Task.Run(() => this.InitializeFallbackCommands(initializationTask));
    }

    private async Task InitializeMediaServiceAsync()
    {
        try
        {
            await this._mediaService.StartAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
            throw;
        }
    }

    private void InitializeFallbackCommands(
        Task initializationTask)
    {
        try
        {
            var play = new FallbackPlayCommandItem(
                new PlayPauseMediaCommand(
                    this._mediaService,
                    initializationTask,
                    this._resultFactory,
                    this._iconService),
                Strings.TogglePlayPause!,
                this._settingsManager,
                this._iconService);
            var skipNext = new FallbackSkipTrackCommandItem(
                this._mediaService,
                initializationTask,
                this._settingsManager,
                this._resultFactory,
                this._iconService);
            var skipPrevious = new FallbackPreviousTrackCommandItem(
                this._mediaService,
                initializationTask,
                this._settingsManager,
                this._resultFactory,
                this._iconService);
            this._fallbackCommandsWithoutVolume = [play, skipNext, skipPrevious];
            this._fallbackCommandsWithVolume =
            [
                play,
                new FallbackUnmuteCommandItem(
                    this._settingsManager,
                    this._systemVolumeService,
                    this._resultFactory,
                    this._iconService),
                new FallbackMuteCommandItem(
                    this._settingsManager,
                    this._systemVolumeService,
                    this._resultFactory,
                    this._iconService),
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

    }

    private void SettingsOnSettingsChanged(object sender, Settings args)
    {
        this.UpdateMediaServiceOptions();
        this.UpdateTopLevelCommands();
        this.UpdateFallbackCommands();
        this.RaiseItemsChanged();
    }

    private void IconServiceOnIconsChanged(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref this._disposeState) != 0)
        {
            return;
        }

        this._volumeCommands[0].UpdateIcon(this._iconService.GetIcon(
            ThemedIcon.VolumeUp,
            IconSurface.CommandPalette));
        this._volumeCommands[1].UpdateIcon(this._iconService.GetIcon(
            ThemedIcon.VolumeDown,
            IconSurface.CommandPalette));

        for (var i = 3; i < this._volumeCommands.Length; i++)
        {
            var percentage = (i - 3) * 25;
            this._volumeCommands[i].UpdateIcon(VolumePresentation.GetThemedIcon(
                percentage,
                this._iconService,
                IconSurface.CommandPalette));
        }

        foreach (var item in this._fallbackCommandsWithVolume)
        {
            if (item is IIconThemeAware themeAwareItem)
            {
                themeAwareItem.RefreshIconTheme();
            }
        }

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

    private void UpdateMediaServiceOptions()
    {
        this._mediaService.UpdateOptions(new(
            this._settingsManager.PauseOthersOnPlay));
    }

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
            this._iconService.IconsChanged -= this.IconServiceOnIconsChanged;
            this._toggleMuteCommandItem.Dispose();
            this._mediaControlsExtensionPage.Dispose();
            this._mediaControlsBand.Dispose();
            ((IDisposable)this._nowPlayingItem).Dispose();
            this._metadataPages.Dispose();
            this._mediaSessionViewModels.Dispose();
            this._mediaService.Dispose();
            this._systemVolumeService.Dispose();
            this._iconService.Dispose();
        }
        finally
        {
            base.Dispose();
        }
    }
}
