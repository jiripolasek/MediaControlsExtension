// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.MediaControlsExtension;

public sealed partial class MediaControlsExtensionCommandsProvider : CommandProvider, IDisposable
{
    private readonly MediaCommandResultFactory _resultFactory;
    private readonly ILogger _logger;
    private readonly MediaService _mediaService;
    private readonly MediaSessionViewModelCache _mediaSessionViewModels;
    private readonly MediaMetadataPageCache _metadataPages;
    private readonly SystemVolumeService _systemVolumeService;
    private readonly SettingsManager _settingsManager = new();
    private readonly IconService _iconService;
    private readonly CommandItem _mediaControlsPageItem;
    private readonly CommandItem _nowPlayingItem;
    private readonly MediaControlsExtensionPage _mediaControlsExtensionPage;
    private readonly MediaControlsExtensionPage _mediaControlsBand;
    private readonly CommandItem _mediaControlsBandItem;
    private readonly VolumeDockBand _volumeDockBand;
    private readonly ToggleMuteCommandItem _toggleMuteCommandItem;
    private readonly CurrentSessionNavigationCommandItem[] _trackNavigationCommands;
    private readonly CommandItem[] _volumeCommands;
    private ICommandItem[] _commands = [];
    private ICommandItem[] _bands = [];
    private int _disposeState;

    public MediaControlsExtensionCommandsProvider()
        : this(NullLoggerFactory.Instance)
    {
    }

    internal MediaControlsExtensionCommandsProvider(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this._logger = loggerFactory.CreateLogger<MediaControlsExtensionCommandsProvider>();
        this._mediaService = new MediaService(loggerFactory);
        this._mediaSessionViewModels = new(this._mediaService, loggerFactory);
        this._systemVolumeService = new(loggerFactory);
        this._iconService = new IconService(this._settingsManager, loggerFactory);
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
            this._iconService,
            loggerFactory);
        this.UpdateMediaServiceOptions();

        this._mediaControlsExtensionPage = new(
            this._mediaService,
            this._mediaSessionViewModels,
            this._metadataPages,
            this._systemVolumeService,
            this._settingsManager,
            this._resultFactory,
            this._iconService,
            loggerFactory);
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
            this._iconService,
            loggerFactory);
#endif
        this._nowPlayingItem = new NowPlayingListItem(
            this._mediaService,
            this._mediaSessionViewModels,
            this._metadataPages,
            this._settingsManager,
            this._resultFactory,
            this._iconService,
            loggerFactory,
            false);
        this._toggleMuteCommandItem = new(
            this._systemVolumeService,
            this._resultFactory,
            this._iconService,
            IconSurface.CommandPalette,
            loggerFactory)
        {
            Title = Strings.Command_ToggleMute!,
        };
        this._volumeCommands =
        [
            new CommandItem(new ChangeVolumeMediaInvokableCommand(VolumeChange.Increase, this._systemVolumeService, this._resultFactory, loggerFactory))
            {
                Title = Strings.Command_VolumeUp!,
                Icon = this._iconService.GetIcon(
                    ThemedIcon.VolumeUp,
                    IconSurface.CommandPalette),
            },
            new CommandItem(new ChangeVolumeMediaInvokableCommand(VolumeChange.Decrease, this._systemVolumeService, this._resultFactory, loggerFactory))
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
                IconSurface.CommandPalette,
                loggerFactory),
        ];
        this._mediaControlsBand = new(
            this._mediaService,
            this._mediaSessionViewModels,
            this._metadataPages,
            this._systemVolumeService,
            this._settingsManager,
            this._resultFactory,
            this._iconService,
            loggerFactory,
            new DockHeadCommandTargets(
                this._mediaControlsExtensionPage,
                currentMediaMetadataPage));
        this._mediaControlsBandItem = new(this._mediaControlsBand) { Title = Strings.Name! };
        this._volumeDockBand = new(
            this._systemVolumeService,
            this._resultFactory,
            this._iconService,
            this._settingsManager.ShowVolumeAdjustmentCommandsInDockBand,
            loggerFactory);
        var initializationTask = Task.Run(this.InitializeMediaServiceAsync);
        this._trackNavigationCommands =
        [
            new CurrentSessionNavigationCommandItem(
                new NextTrackInvokableMediaCommand(
                    this._mediaService,
                    initializationTask,
                    this._resultFactory,
                    loggerFactory),
                this._mediaService,
                this._mediaSessionViewModels,
                this._iconService,
                ThemedIcon.SkipNext)
            {
                Title = Strings.Command_NextTrack!,
            },
            new CurrentSessionNavigationCommandItem(
                new PreviousTrackInvokableMediaCommand(
                    this._mediaService,
                    initializationTask,
                    this._resultFactory,
                    loggerFactory),
                this._mediaService,
                this._mediaSessionViewModels,
                this._iconService,
                ThemedIcon.SkipPrevious)
            {
                Title = Strings.Command_PreviousTrack!,
            },
        ];
        this.UpdateTopLevelCommands();
        this.UpdateDockBands();
    }

    private async Task InitializeMediaServiceAsync()
    {
        try
        {
            await this._mediaService.StartAsync();
        }
        catch (Exception ex)
        {
            ExtensionLog.UnexpectedError(this._logger, ex);
            throw;
        }
    }

    private void SettingsOnSettingsChanged(object sender, Settings args)
    {
        this.UpdateMediaServiceOptions();
        this.UpdateTopLevelCommands();
        this.UpdateDockBands();
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

        this.RaiseItemsChanged();
    }

    private void UpdateTopLevelCommands()
    {
        List<ICommandItem> commands = [this._mediaControlsPageItem];
        if (this._settingsManager.ShowCurrentMediaAtTopLevel)
        {
            commands.Add(this._nowPlayingItem);
        }

        if (this._settingsManager.ShowTrackNavigationCommandsAtTopLevel)
        {
            commands.AddRange(this._trackNavigationCommands);
        }

        if (this._settingsManager.EnableVolumeControls)
        {
            commands.AddRange(this._volumeCommands);
        }

        this._commands = [.. commands];
    }

    private void UpdateMediaServiceOptions()
    {
        this._mediaService.UpdateOptions(new(
            this._settingsManager.PauseOthersOnPlay));
    }

    private void UpdateDockBands()
    {
        this._volumeDockBand.UpdateAdjustmentCommandsVisibility(
            this._settingsManager.ShowVolumeAdjustmentCommandsInDockBand);
        this._bands = this._settingsManager.EnableVolumeControls
            ? [this._mediaControlsBandItem, this._volumeDockBand]
            : [this._mediaControlsBandItem];
    }

    public override ICommandItem[] TopLevelCommands() => this._commands;

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
            foreach (var item in this._trackNavigationCommands)
            {
                item.Dispose();
            }

            this._mediaControlsExtensionPage.Dispose();
            this._mediaControlsBand.Dispose();
            this._volumeDockBand.Dispose();
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
