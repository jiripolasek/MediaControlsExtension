// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Services;

internal sealed partial class MediaService : INotifyPropertyChanged, IDisposable
{
    public event EventHandler? LoadingStatusChanged;
    public event EventHandler? Initialized;
    public event EventHandler? CurrentMediaPlaybackChanged;
    public event EventHandler? MediaSourcesChanged;
    public event EventHandler<MediaSource?>? CurrentMediaSourceChanged;

    private readonly ThrottledAction _currentMediaPlaybackChangedAction;
    private readonly ThrottledAction _currentMediaSourceChangedAction;
    private readonly ThrottledAction _mediaSourcesChangedAction;

    private readonly ThrottledAction _refreshAction;

    private readonly List<MediaSource> _sources = [];
    private readonly Dictionary<string, MediaSource> _sourcesByAppId = new();

    private bool _disposed;
    private bool _hasPendingCurrentMediaPlaybackChanged;
    private bool _hasPendingCurrentMediaSourceChanged;
    private bool _hasPendingMediaSourcesChanged;

    private MediaSource? _pendingCurrentMediaSource;
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;

    public MediaSource? CurrentSource
    {
        get;
        private set
        {
            if (Equals(field, value))
            {
                return;
            }

            if (field != null)
            {
                field.PlaybackInfoChanged -= this.OnCurrentMediaPlaybackInfoChanged;
                field.MediaPropertiesUpdated -= this.OnCurrentMediaPlaybackInfoChanged;
            }

            field = value;

            if (field != null)
            {
                field.PlaybackInfoChanged += this.OnCurrentMediaPlaybackInfoChanged;
                field.MediaPropertiesUpdated += this.OnCurrentMediaPlaybackInfoChanged;
            }

            this.PropertyChanged?.Invoke(this, new(nameof(this.CurrentSource)));

            this._pendingCurrentMediaSource = value;
            this._hasPendingCurrentMediaSourceChanged = true;
            this._currentMediaSourceChangedAction.Invoke();
        }
    }

    public IEnumerable<MediaSource> Sources => this._sources;

    public bool IsLoading
    {
        get;
        private set
        {
            if (this.SetField(ref field, value))
            {
                this.LoadingStatusChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    internal GlobalSystemMediaTransportControlsSessionManager SessionManager => this._sessionManager ?? throw new InvalidOperationException("MediaService is not initialized. Call InitializeAsync first.");

    public MediaService()
    {
        this._refreshAction = new(100, this.RefreshCore);
        this._mediaSourcesChangedAction = new(100, this.FireMediaSourcesChanged);
        this._currentMediaSourceChangedAction = new(100, this.FireCurrentMediaSourceChanged);
        this._currentMediaPlaybackChangedAction = new(100, this.FireCurrentMediaPlaybackChanged);
    }

    public void Dispose()
    {
        if (this._disposed)
        {
            return;
        }

        if (this._sessionManager != null)
        {
            this._sessionManager!.SessionsChanged -= this.SessionManagerOnSessionsChanged;
            this._sessionManager!.CurrentSessionChanged -= this.SessionManagerOnCurrentSessionChanged;
        }

        foreach (var source in this._sourcesByAppId.Values)
        {
            source.Dispose();
        }

        this.CurrentSource = null;
        this._sources.Clear();
        this._sourcesByAppId.Clear();

        this._refreshAction.Dispose();
        this._mediaSourcesChangedAction.Dispose();
        this._currentMediaSourceChangedAction.Dispose();
        this._currentMediaPlaybackChangedAction.Dispose();

        this._disposed = true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnCurrentMediaPlaybackInfoChanged(object? sender, EventArgs e)
    {
        this._hasPendingCurrentMediaPlaybackChanged = true;
        this._currentMediaPlaybackChangedAction.Invoke();
    }

    public async Task InitializeAsync()
    {
        Logger.LogInformation("Media Service initialization started...");

        this._sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        this._sessionManager.SessionsChanged += this.SessionManagerOnSessionsChanged;
        this._sessionManager.CurrentSessionChanged += this.SessionManagerOnCurrentSessionChanged;

        this.Initialized?.Invoke(this, EventArgs.Empty);

        this.UpdateCurrentSource();
        this.Refresh();
    }

    private void UpdateCurrentSource()
    {
        var currentSession = this._sessionManager?.GetCurrentSession();
        this.CurrentSource = currentSession != null ? this._sourcesByAppId.GetValueOrDefault(currentSession.SourceAppUserModelId) ?? new MediaSource(currentSession) : null;
    }

    private void Refresh()
    {
        this._refreshAction.Invoke();
    }

    private void RefreshCore()
    {
        this.IsLoading = true;
        try
        {
            var sessions = this._sessionManager?.GetSessions() ?? [];
            var currentAppIds = sessions.Select(s => s.SourceAppUserModelId).ToHashSet();
            var changed = false;

            // Remove sources that no longer exist
            var toRemove = this._sourcesByAppId.Keys.Except(currentAppIds).ToList();
            foreach (var appId in toRemove)
            {
                if (this._sourcesByAppId.TryGetValue(appId, out var source))
                {
                    this._sources.Remove(source);
                    this._sourcesByAppId.Remove(appId);

                    try
                    {
                        source.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex);
                    }

                    changed = true;
                }
            }

            // Add new sources or update existing ones with fresh session instances
            foreach (var session in sessions)
            {
                var appId = session.SourceAppUserModelId;

                if (this._sourcesByAppId.TryGetValue(appId, out var existingSource))
                {
                    existingSource.UpdateSession(session);
                }
                else
                {
                    var newSource = new MediaSource(session);
                    this._sources.Add(newSource);
                    this._sourcesByAppId[appId] = newSource;
                    changed = true;
                }
            }

            if (changed)
            {
                this._hasPendingMediaSourcesChanged = true;
                this._mediaSourcesChangedAction.Invoke();
            }

            this.UpdateCurrentSource();
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation
        }
        finally
        {
            this.IsLoading = false;
        }
    }

    private void SessionManagerOnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        this.UpdateCurrentSource();
    }

    private void SessionManagerOnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        this.Refresh();
    }
    
    private void FireMediaSourcesChanged()
    {
        if (this._hasPendingMediaSourcesChanged)
        {
            this._hasPendingMediaSourcesChanged = false;
            this.MediaSourcesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void FireCurrentMediaSourceChanged()
    {
        if (this._hasPendingMediaSourcesChanged)
        {
            this.FireMediaSourcesChanged();
        }

        if (this._hasPendingCurrentMediaSourceChanged)
        {
            this._hasPendingCurrentMediaSourceChanged = false;
            this._hasPendingCurrentMediaPlaybackChanged = false;
            var source = this._pendingCurrentMediaSource;
            this.CurrentMediaSourceChanged?.Invoke(this, source);
            this.CurrentMediaPlaybackChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void FireCurrentMediaPlaybackChanged()
    {
        if (this._hasPendingCurrentMediaPlaybackChanged && !this._hasPendingCurrentMediaSourceChanged)
        {
            this._hasPendingCurrentMediaPlaybackChanged = false;
            this.CurrentMediaPlaybackChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        this.PropertyChanged?.Invoke(this, new(propertyName));
        return true;
    }
}