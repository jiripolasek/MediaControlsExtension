// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class VolumeListItem : ListItem, IDisposable
{
    internal const string CommandId = "com.jpsoftworks.cmdpal.mediacontrols.volume";

    private readonly Lock _presentationLock = new();
    private readonly SystemVolumeService _systemVolumeService;
    private readonly ToggleMuteMediaInvokableCommand _toggleMuteCommand;
    private readonly IIconService _iconService;
    private readonly IconSurface _iconSurface;

    private bool _disposed;
    private bool _hasObservedState;
    private SystemVolumeState? _lastState;

    public VolumeListItem(
        SystemVolumeService systemVolumeService,
        MediaCommandResultFactory resultFactory,
        IIconService iconService,
        IconSurface iconSurface)
        : this(
            new(systemVolumeService, resultFactory),
            systemVolumeService,
            resultFactory,
            iconService,
            iconSurface)
    {
    }

    private VolumeListItem(
        ToggleMuteMediaInvokableCommand toggleMuteCommand,
        SystemVolumeService systemVolumeService,
        MediaCommandResultFactory resultFactory,
        IIconService iconService,
        IconSurface iconSurface)
        : base(toggleMuteCommand)
    {
        ArgumentNullException.ThrowIfNull(systemVolumeService);
        ArgumentNullException.ThrowIfNull(resultFactory);
        ArgumentNullException.ThrowIfNull(iconService);

        this._toggleMuteCommand = toggleMuteCommand;
        this._systemVolumeService = systemVolumeService;
        this._iconService = iconService;
        this._iconSurface = iconSurface;

        this._toggleMuteCommand.Id = CommandId;

        this.Title = Strings.Toast_Volume!;
        this.Icon = iconService.GetIcon(ThemedIcon.ToggleMute, iconSurface);
        this.MoreCommands = CreateMoreCommands(systemVolumeService, resultFactory);

        // Subscribe before seeding. If a notification wins the race with the initial
        // read, _hasObservedState prevents the older seed from replacing it.
        this._systemVolumeService.StateChanged += this.SystemVolumeServiceOnStateChanged;
        this._iconService.IconsChanged += this.IconServiceOnIconsChanged;

        try
        {
            var state = this._systemVolumeService.TryGetCurrentState(out var currentState)
                ? currentState
                : this._systemVolumeService.GetState(CancellationToken.None);
            lock (this._presentationLock)
            {
                if (!this._disposed && !this._hasObservedState)
                {
                    this.ApplyState(state);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Could not initialize the volume list item: {ex.Message}");
        }
    }

    private static IContextItem[] CreateMoreCommands(
        SystemVolumeService systemVolumeService,
        MediaCommandResultFactory resultFactory)
    {
        return
        [
            new CommandContextItem(new ChangeVolumeMediaInvokableCommand(VolumeChange.Increase, systemVolumeService, resultFactory))
            {
                RequestedShortcut = Chords.VolumeUp,
                Icon = Icons.Volume_Up,
            },
            new CommandContextItem(new ChangeVolumeMediaInvokableCommand(VolumeChange.Decrease, systemVolumeService, resultFactory))
            {
                RequestedShortcut = Chords.VolumeDown,
                Icon = Icons.Volume_Down,
            },
            new Separator(),
            .. VolumeCommandFactory.CreatePresetContextItems(systemVolumeService, resultFactory),
        ];
    }

    private void SystemVolumeServiceOnStateChanged(object? sender, SystemVolumeState state)
    {
        lock (this._presentationLock)
        {
            if (this._disposed)
            {
                return;
            }

            this._hasObservedState = true;
            this.ApplyState(state);
        }
    }

    private void ApplyState(SystemVolumeState state)
    {
        if (this._lastState == state)
        {
            return;
        }

        this._lastState = state;
        var icon = VolumePresentation.GetThemedIcon(
            state,
            this._iconService,
            this._iconSurface);
        this.Icon = icon;
        this._toggleMuteCommand.UpdateIcon(icon);

        this.Title = VolumePresentation.FormatStatus(state);
        this.Subtitle = state.IsMuted
            ? VolumePresentation.FormatLevel(state.VolumePercent)
            : string.Empty;
    }

    public void Dispose()
    {
        lock (this._presentationLock)
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            this._systemVolumeService.StateChanged -= this.SystemVolumeServiceOnStateChanged;
            this._iconService.IconsChanged -= this.IconServiceOnIconsChanged;
        }
    }

    private void IconServiceOnIconsChanged(object? sender, EventArgs args)
    {
        lock (this._presentationLock)
        {
            if (this._disposed)
            {
                return;
            }

            if (this._lastState is { } state)
            {
                this._lastState = null;
                this.ApplyState(state);
            }
            else
            {
                var icon = this._iconService.GetIcon(ThemedIcon.ToggleMute, this._iconSurface);
                this.Icon = icon;
                this._toggleMuteCommand.UpdateIcon(icon);
            }
        }
    }
}
