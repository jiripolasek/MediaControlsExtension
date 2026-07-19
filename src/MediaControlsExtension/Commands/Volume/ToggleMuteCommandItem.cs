// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class ToggleMuteCommandItem : CommandItem, IDisposable
{
    private readonly ToggleMuteIconBinding _iconBinding;

    public ToggleMuteCommandItem(SystemVolumeService systemVolumeService, YetAnotherHelper yetAnotherHelper)
        : this(new(systemVolumeService, yetAnotherHelper), systemVolumeService)
    {
    }

    private ToggleMuteCommandItem(
        ToggleMuteMediaInvokableCommand command,
        SystemVolumeService systemVolumeService)
        : base(command)
    {
        this._iconBinding = new(systemVolumeService, command, this.SetIcon);
    }

    private void SetIcon(IconInfo icon)
    {
        this.UpdateIcon(icon);
    }

    public void Dispose() => this._iconBinding.Dispose();
}

internal sealed partial class ToggleMuteIconBinding : IDisposable
{
    private readonly Lock _presentationLock = new();
    private readonly SystemVolumeService _systemVolumeService;
    private readonly ToggleMuteMediaInvokableCommand _command;
    private readonly Action<IconInfo> _setItemIcon;

    private bool _disposed;
    private bool _hasObservedState;
    private SystemVolumeState? _lastState;

    public ToggleMuteIconBinding(
        SystemVolumeService systemVolumeService,
        ToggleMuteMediaInvokableCommand command,
        Action<IconInfo> setItemIcon)
    {
        this._systemVolumeService = systemVolumeService;
        this._command = command;
        this._setItemIcon = setItemIcon;

        this.ApplyIcon(Icons.ToggleMute);
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
            Logger.LogWarning($"Could not initialize the mute command icon: {ex.Message}");
        }
    }

    private void SystemVolumeServiceOnStateChanged(object? sender, SystemVolumeState state)
    {
        lock (this._presentationLock)
        {
            if (!this._disposed)
            {
                this._hasObservedState = true;
                this.ApplyState(state);
            }
        }
    }

    private void ApplyState(SystemVolumeState state)
    {
        if (this._lastState == state)
        {
            return;
        }

        this._lastState = state;
        this.ApplyIcon(VolumePresentation.GetIcon(state));
    }

    private void ApplyIcon(IconInfo icon)
    {
        this._setItemIcon(icon);
        this._command.UpdateIcon(icon);
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
