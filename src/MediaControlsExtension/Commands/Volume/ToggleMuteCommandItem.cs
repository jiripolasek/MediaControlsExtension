// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class ToggleMuteCommandItem : CommandItem, IDisposable
{
    private readonly ToggleMuteIconBinding _iconBinding;

    public ToggleMuteCommandItem(
        SystemVolumeService systemVolumeService,
        MediaCommandResultFactory resultFactory,
        IIconService iconService,
        IconSurface iconSurface,
        ILoggerFactory loggerFactory)
        : this(
            new(systemVolumeService, resultFactory, loggerFactory),
            systemVolumeService,
            iconService,
            iconSurface,
            loggerFactory)
    {
    }

    private ToggleMuteCommandItem(
        ToggleMuteMediaInvokableCommand command,
        SystemVolumeService systemVolumeService,
        IIconService iconService,
        IconSurface iconSurface,
        ILoggerFactory loggerFactory)
        : base(command)
    {
        this._iconBinding = new(
            systemVolumeService,
            command,
            iconService,
            iconSurface,
            this.SetIcon,
            loggerFactory);
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
    private readonly IIconService _iconService;
    private readonly IconSurface _iconSurface;
    private readonly Action<IconInfo> _setItemIcon;
    private readonly ILogger _logger;

    private bool _disposed;
    private bool _hasObservedState;
    private SystemVolumeState? _lastState;

    public ToggleMuteIconBinding(
        SystemVolumeService systemVolumeService,
        ToggleMuteMediaInvokableCommand command,
        IIconService iconService,
        IconSurface iconSurface,
        Action<IconInfo> setItemIcon,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this._systemVolumeService = systemVolumeService;
        this._command = command;
        this._iconService = iconService;
        this._iconSurface = iconSurface;
        this._setItemIcon = setItemIcon;
        this._logger = loggerFactory.CreateLogger<ToggleMuteIconBinding>();

        this.ApplyIcon(iconService.GetIcon(ThemedIcon.ToggleMute, iconSurface));
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
            ExtensionLog.Warning(
                this._logger,
                $"Could not initialize the mute command icon: {ex.Message}");
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
        this.ApplyIcon(VolumePresentation.GetThemedIcon(
            state,
            this._iconService,
            this._iconSurface));
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
                this.ApplyIcon(this._iconService.GetIcon(ThemedIcon.ToggleMute, this._iconSurface));
            }
        }
    }
}
