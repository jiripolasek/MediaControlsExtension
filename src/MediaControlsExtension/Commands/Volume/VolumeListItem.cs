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

    private bool _disposed;
    private bool _hasObservedState;
    private SystemVolumeState? _lastState;

    public VolumeListItem(
        SystemVolumeService systemVolumeService,
        YetAnotherHelper yetAnotherHelper)
        : this(
            new(systemVolumeService, yetAnotherHelper),
            systemVolumeService,
            yetAnotherHelper)
    {
    }

    private VolumeListItem(
        ToggleMuteMediaInvokableCommand toggleMuteCommand,
        SystemVolumeService systemVolumeService,
        YetAnotherHelper yetAnotherHelper)
        : base(toggleMuteCommand)
    {
        ArgumentNullException.ThrowIfNull(systemVolumeService);
        ArgumentNullException.ThrowIfNull(yetAnotherHelper);

        this._toggleMuteCommand = toggleMuteCommand;
        this._systemVolumeService = systemVolumeService;

        this._toggleMuteCommand.Id = CommandId;

        this.Title = Strings.Toast_Volume!;
        this.Subtitle = string.Empty;
        this.Icon = Icons.ToggleMute;
        this.MoreCommands = CreateMoreCommands(systemVolumeService, yetAnotherHelper);

        // Subscribe before seeding. If a notification wins the race with the initial
        // read, _hasObservedState prevents the older seed from replacing it.
        this._systemVolumeService.StateChanged += this.SystemVolumeServiceOnStateChanged;

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
        YetAnotherHelper yetAnotherHelper)
    {
        return
        [
            new CommandContextItem(new ChangeVolumeMediaInvokableCommand(VolumeChange.Increase, systemVolumeService, yetAnotherHelper))
            {
                RequestedShortcut = Chords.VolumeUp,
                Icon = Icons.Volume_Max,
            },
            new CommandContextItem(new ChangeVolumeMediaInvokableCommand(VolumeChange.Decrease, systemVolumeService, yetAnotherHelper))
            {
                RequestedShortcut = Chords.VolumeDown,
                Icon = Icons.Volume_Low,
            },
            new Separator(),
            .. VolumeCommandFactory.CreatePresetContextItems(systemVolumeService, yetAnotherHelper),
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
        var icon = VolumePresentation.GetIcon(state);
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
        }
    }
}
