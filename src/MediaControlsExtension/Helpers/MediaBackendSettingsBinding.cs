using JPSoftworks.MediaControlsExtension.Pages;

namespace JPSoftworks.MediaControlsExtension.Helpers;

/// <summary>Connects settings to backend selection and an existing page; the caller owns both.</summary>
internal sealed partial class MediaBackendSettingsBinding : IDisposable
{
    private readonly MediaBackendSettings _settings;
    private readonly MediaSourcesPage _page;
    private readonly Action<string?> _updateEnabled;
    private int _disposed;

    public MediaBackendSettingsBinding(MediaBackendSettings settings, MediaSourcesPage page, Action<string?> updateEnabled)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(updateEnabled);
        this._settings = settings;
        this._page = page;
        this._updateEnabled = updateEnabled;
        settings.EnabledChanged += this.OnEnabledChanged;
        try { this.ApplySettings(null); }
        catch
        {
            this.Dispose();
            throw;
        }
    }

    private void OnEnabledChanged(object? sender, MediaBackendEnabledChangedEventArgs args) =>
        this.ApplySettings(args.Enabled ? args.BackendId : null);

    private void ApplySettings(string? retryBackendId)
    {
        if (Volatile.Read(ref this._disposed) != 0) { return; }
        this._updateEnabled(retryBackendId);
        this._page.RefreshConfiguration();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref this._disposed, 1) == 0)
        {
            this._settings.EnabledChanged -= this.OnEnabledChanged;
        }
    }
}