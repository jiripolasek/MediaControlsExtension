// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Pages;

internal sealed partial class MediaControlsExtensionPage : ListPage
{
    private readonly SettingsManager _settingsManager;
    private readonly YetAnotherHelper _yetAnotherHelper;
    private readonly MediaService _mediaService;
    private readonly Lock _refreshLock = new();
    private readonly bool _isBandPage;

    private bool _isInitialized;
    private readonly ListItem? _playPauseCurrentSessionItem;
    private readonly ListItem? _nextTrackCurrentSessionItem;
    private readonly ListItem? _prevTrackCurrentSessionItem;
    private readonly ListItem? _muteCommandItem;
    private List<MediaSourceListItem> _items = [];

    public MediaControlsExtensionPage(
        MediaService mediaService,
        SettingsManager settingsManager,
        YetAnotherHelper yetAnotherHelper,
        bool asBandPage = false)
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(settingsManager);
        ArgumentNullException.ThrowIfNull(yetAnotherHelper);

        this._isBandPage = asBandPage;
        this._settingsManager = settingsManager;
        this._yetAnotherHelper = yetAnotherHelper;
        this._mediaService = mediaService;

        this.Icon = Icons.MainIcon;
        this.Title = Strings.Name!;
        this.Name = Strings.Open!;
        this.Id = "com.jpsoftworks.cmdpal.mediacontrols";
        this.PlaceholderText = "Search media commands or sessions…";

        this._mediaService.Initialized += (_, _) =>
        {
            this._isInitialized = true;
            this.RaiseItemsChanged();
        };

        this._mediaService.MediaSourcesChanged += (_, _) =>
        {
            var oldItems = this._items.ToArray();

            List<MediaSourceListItem> mediaSourceListItems = [.. this._mediaService.Sources.Select(mediaSource => new MediaSourceListItem(this._mediaService, mediaSource, this._settingsManager, this._yetAnotherHelper))];
            lock (this._refreshLock)
            {
                this._items = mediaSourceListItems;
            }

            _ = Task.Run(() =>
            {
                foreach (var item in oldItems)
                {
                    try
                    {

                        item.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex);
                    }
                }
            });

            this.RaiseItemsChanged();
        };

        this._mediaService.CurrentMediaSourceChanged += (_, _) => this.UpdateCurrentMediaItems();

        this._mediaService.CurrentMediaPlaybackChanged += (_, _) => this.UpdateCurrentMediaItems();

        this._mediaService.LoadingStatusChanged += (_, _) => this.IsLoading = this._mediaService.IsLoading;

        this.EmptyContent = new CommandItem
        {
            Title = "No media sources available",
            Subtitle = "No media sources are currently available. Please ensure that you have media applications running that support media controls.",
            Icon = Icons.MainIcon
        };

        this._playPauseCurrentSessionItem = new NowPlayingListItem(this._mediaService, this._settingsManager, this._yetAnotherHelper, this._isBandPage);
        this._nextTrackCurrentSessionItem = new(new MediaCurrentSessionCommand(this._mediaService, MediaSessionOperations.SkipNextTrack, this._yetAnotherHelper)) { Title = Strings.Command_NextTrack, Subtitle = Strings.Command_NextTrack_Subtitle, Icon = Icons.SkipNextTrack };
        this._prevTrackCurrentSessionItem = new(new MediaCurrentSessionCommand(this._mediaService, MediaSessionOperations.SkipPreviousTrack, this._yetAnotherHelper)) { Title = Strings.Command_PreviousTrack, Subtitle = Strings.Command_PreviousTrack_Subtitle, Icon = Icons.SkipPreviousTrack };
        this._muteCommandItem = new(new ToggleMuteMediaInvokableCommand(this._yetAnotherHelper));

        if (this._isBandPage)
        {
            this._playPauseCurrentSessionItem.Title = string.Empty;
            this._playPauseCurrentSessionItem.Subtitle = string.Empty;
            this._nextTrackCurrentSessionItem.Title = string.Empty;
            this._nextTrackCurrentSessionItem.Subtitle = string.Empty;
            this._prevTrackCurrentSessionItem.Title = string.Empty;
            this._prevTrackCurrentSessionItem.Subtitle = string.Empty;
            this._muteCommandItem.Title = string.Empty;
            this._muteCommandItem.Subtitle = string.Empty;
        }
    }

    private void UpdateCurrentMediaItems()
    {
        if (!this._settingsManager.ShowSkipCommands)
        {
            return;
        }

        if (this._nextTrackCurrentSessionItem?.Command is MediaCurrentSessionCommand nextTrackCommand)
        {
            this._nextTrackCurrentSessionItem.UpdateIcon(nextTrackCommand.CanExecute() ? Icons.SkipNextTrack : Icons.SkipNextTrackDisabled);
        }
        if (this._prevTrackCurrentSessionItem?.Command is MediaCurrentSessionCommand prevTrackCommand)
        {
            this._prevTrackCurrentSessionItem.UpdateIcon(prevTrackCommand.CanExecute() ? Icons.SkipPreviousTrack : Icons.SkipPreviousTrackDisabled);
        }

        // don't refresh items - it causes reset of the selected item
    }

    public override IListItem[] GetItems()
    {
        if (this._isBandPage)
        {
            return this.GetBandItems().ToArray();
        }

        if (!this._isInitialized)
        {
            this.IsLoading = true;
            return [.. this.GetGlobalCommands()];
        }

        return
        [
            ..this.GetGlobalCommands(),
            ..this._items
        ];
    }

    private List<IListItem> GetGlobalCommands()
    {
        List<IListItem> items = [];

        if (this._playPauseCurrentSessionItem != null)
        {
            items.Add(this._playPauseCurrentSessionItem);
        }

        if (this._settingsManager.ShowSkipCommands)
        {
            items.Add(this._nextTrackCurrentSessionItem!);
            items.Add(this._prevTrackCurrentSessionItem!);
        }

        items.Add(this._muteCommandItem!);

        return items;
    }
    private List<IListItem> GetBandItems()
    {
        List<IListItem> items = [];
        if (this._playPauseCurrentSessionItem != null && this._items.Count > 0)
        {
            items.Add(this._items.First());
            items.Add(this._prevTrackCurrentSessionItem!);
            items.Add(this._playPauseCurrentSessionItem);
            items.Add(this._nextTrackCurrentSessionItem!);
        }
        return items;
    }

}