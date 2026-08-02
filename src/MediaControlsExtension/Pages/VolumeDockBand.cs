// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Pages;

internal sealed partial class VolumeDockBand : WrappedDockItem, IDisposable
{
    private readonly VolumeListItem _volumeItem;
    private readonly ListItem _volumeDownItem;
    private readonly ListItem _volumeUpItem;
    private readonly IIconService _iconService;
    private bool _showAdjustmentCommands;
    private int _disposeState;

    public VolumeDockBand(
        SystemVolumeService systemVolumeService,
        MediaCommandResultFactory resultFactory,
        IIconService iconService,
        bool showAdjustmentCommands,
        ILoggerFactory loggerFactory)
        : this(
            new(
                systemVolumeService,
                resultFactory,
                iconService,
                IconSurface.Dock,
                VolumeListItemPresentation.Dock,
                loggerFactory),
            CreateVolumeChangeItem(
                VolumeChange.Decrease,
                systemVolumeService,
                resultFactory,
                iconService,
                loggerFactory),
            CreateVolumeChangeItem(
                VolumeChange.Increase,
                systemVolumeService,
                resultFactory,
                iconService,
                loggerFactory),
            iconService,
            showAdjustmentCommands)
    {
    }

    private VolumeDockBand(
        VolumeListItem volumeItem,
        ListItem volumeDownItem,
        ListItem volumeUpItem,
        IIconService iconService,
        bool showAdjustmentCommands)
        : base(
            [volumeItem],
            // Sharing the top-level volume command ID makes Pin to Dock resolve
            // to this Dock-themed band instead of creating a generic pinned item.
            VolumeListItem.CommandId,
            Strings.Page_Section_SystemVolume!)
    {
        this._volumeItem = volumeItem;
        this._volumeDownItem = volumeDownItem;
        this._volumeUpItem = volumeUpItem;
        this._iconService = iconService;

        this.UpdateIcons();
        this.UpdateAdjustmentCommandsVisibility(showAdjustmentCommands);
        this._iconService.IconsChanged += this.IconServiceOnIconsChanged;
    }

    private static ListItem CreateVolumeChangeItem(
        VolumeChange change,
        SystemVolumeService systemVolumeService,
        MediaCommandResultFactory resultFactory,
        IIconService iconService,
        ILoggerFactory loggerFactory)
    {
        var command = new ChangeVolumeMediaInvokableCommand(
            change,
            systemVolumeService,
            resultFactory,
            loggerFactory)
        {
            // Dock items are icon-only. The command name must also be empty so
            // ListItem.Title cannot fall back to a visible label.
            Name = string.Empty,
        };
        return new(command)
        {
            Title = string.Empty,
            Subtitle = string.Empty,
            Icon = iconService.GetIcon(
                change == VolumeChange.Increase
                    ? ThemedIcon.VolumeUp
                    : ThemedIcon.VolumeDown,
                IconSurface.Dock),
        };
    }

    private void IconServiceOnIconsChanged(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref this._disposeState) == 0)
        {
            this.UpdateIcons();
        }
    }

    private void UpdateIcons()
    {
        this._volumeDownItem.UpdateIcon(this._iconService.GetIcon(
            ThemedIcon.VolumeDown,
            IconSurface.Dock));
        this._volumeUpItem.UpdateIcon(this._iconService.GetIcon(
            ThemedIcon.VolumeUp,
            IconSurface.Dock));
        this.UpdateIcon(this._iconService.GetIcon(
            ThemedIcon.ToggleMute,
            IconSurface.Dock));
    }

    public void UpdateAdjustmentCommandsVisibility(bool showAdjustmentCommands)
    {
        if (Volatile.Read(ref this._disposeState) != 0 ||
            this._showAdjustmentCommands == showAdjustmentCommands)
        {
            return;
        }

        this._showAdjustmentCommands = showAdjustmentCommands;
        this.Items = showAdjustmentCommands
            ? [this._volumeDownItem, this._volumeItem, this._volumeUpItem]
            : [this._volumeItem];
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref this._disposeState, 1) != 0)
        {
            return;
        }

        this._iconService.IconsChanged -= this.IconServiceOnIconsChanged;
        this._volumeItem.Dispose();
    }
}
