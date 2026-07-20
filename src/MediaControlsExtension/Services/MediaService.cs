// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Services;

internal sealed partial class MediaService : INotifyPropertyChanged, IDisposable
{
    private const int MaxLoggedSessionDiagnostics = 16;
    private const int MaxConsecutiveRefreshFailures = 3;
    private static readonly TimeSpan RefreshRetryDelay = TimeSpan.FromMilliseconds(500);

    [Flags]
    private enum NotificationFlags
    {
        None = 0,
        MediaSourcesChanged = 1 << 0,
        CurrentMediaSourceChanged = 1 << 1,
        CurrentMediaPlaybackChanged = 1 << 2,
        IsLoadingChanged = 1 << 3
    }

    public event EventHandler? LoadingStatusChanged;
    public event EventHandler? Initialized;
    public event EventHandler? CurrentMediaPlaybackChanged;
    public event EventHandler? MediaSourcesChanged;
    public event EventHandler<MediaSource?>? CurrentMediaSourceChanged;

    private readonly ThrottledAction _notificationAction;

    private readonly ThrottledAction _refreshAction;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly CancellationToken _disposeToken;
    private readonly Lock _lifecycleLock = new();

    private readonly List<MediaSource> _sources = [];
    private readonly Dictionary<string, MediaSource> _sourcesByAppId = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loggedSessionDiagnosticKeys = [];

    private bool _disposed;
    private bool _isLoading;
    private int _consecutiveRefreshFailures;
    private NotificationFlags _pendingNotifications;

    private MediaSource? _currentSource;
    private MediaSource? _pendingCurrentMediaSource;
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;

    public MediaSource? CurrentSource
    {
        get
        {
            lock (this._lifecycleLock)
            {
                return this._currentSource;
            }
        }
    }

    public MediaSource[] Sources
    {
        get
        {
            lock (this._lifecycleLock)
            {
                return [.. this._sources];
            }
        }
    }

    public bool IsLoading
    {
        get
        {
            lock (this._lifecycleLock)
            {
                return this._isLoading;
            }
        }
    }

    internal GlobalSystemMediaTransportControlsSessionManager SessionManager =>
        this.TryGetSessionManager(out var manager)
            ? manager
            : throw new InvalidOperationException("MediaService is not initialized. Call InitializeAsync first.");

    internal bool TryGetSessionManager(
        [NotNullWhen(true)] out GlobalSystemMediaTransportControlsSessionManager? manager)
    {
        lock (this._lifecycleLock)
        {
            manager = this._sessionManager;
            return manager is not null;
        }
    }

    public MediaService()
    {
        this._disposeToken = this._disposeCts.Token;
        this._refreshAction = new(100, this.RefreshCoreAsync);
        this._notificationAction = new(100, this.FirePendingNotifications);
    }

    public void Dispose()
    {
        GlobalSystemMediaTransportControlsSessionManager? managerToUnhook;
        MediaSource[] sourcesToDispose;
        lock (this._lifecycleLock)
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            managerToUnhook = this._sessionManager;
            sourcesToDispose = [.. this._sourcesByAppId.Values];

            this.SetCurrentSourceUnderLock(null);
            this._sources.Clear();
            this._sourcesByAppId.Clear();
            this._sessionManager = null;
            this._isLoading = false;
            this._pendingNotifications = NotificationFlags.None;
            this._pendingCurrentMediaSource = null;
        }

        this._disposeCts.Cancel();
        this._refreshAction.Dispose();
        this._notificationAction.Dispose();

        DisposeSources(sourcesToDispose);

        if (managerToUnhook is not null)
        {
            _ = GsmtcOperationGate.RunDetached(() => this.UnhookSessionManagerAsync(managerToUnhook));
        }

        this._disposeCts.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnCurrentMediaPlaybackInfoChanged(object? sender, EventArgs e)
    {
        lock (this._lifecycleLock)
        {
            if (this._disposed)
            {
                return;
            }

            this.QueueNotificationsUnderLock(NotificationFlags.CurrentMediaPlaybackChanged);
        }
    }

    public async Task InitializeAsync()
    {
        Logger.LogInformation("Media Service initialization started...");

        var manager = await GsmtcOperationGate.RunAsync(
            async _ =>
            {
                var requestedManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                requestedManager.SessionsChanged += this.SessionManagerOnSessionsChanged;
                requestedManager.CurrentSessionChanged += this.SessionManagerOnCurrentSessionChanged;
                return requestedManager;
            });

        var disposeManager = false;
        lock (this._lifecycleLock)
        {
            if (this._disposed)
            {
                disposeManager = true;
            }
            else
            {
                this._sessionManager = manager;
            }
        }

        if (disposeManager)
        {
            _ = GsmtcOperationGate.RunDetached(() => this.UnhookSessionManagerAsync(manager));
            return;
        }

        this.Initialized?.Invoke(this, EventArgs.Empty);

        this.Refresh();
    }

    internal MediaSource? FindSourceForSession(GlobalSystemMediaTransportControlsSession session)
    {
        GsmtcOperationGate.VerifyAccess();
        ArgumentNullException.ThrowIfNull(session);

        var appId = session.SourceAppUserModelId;
        lock (this._lifecycleLock)
        {
            return this._sourcesByAppId.GetValueOrDefault(appId);
        }
    }

    private bool SetCurrentSourceUnderLock(MediaSource? source)
    {
        if (ReferenceEquals(this._currentSource, source))
        {
            return false;
        }

        if (this._currentSource is not null)
        {
            this._currentSource.PlaybackInfoChanged -= this.OnCurrentMediaPlaybackInfoChanged;
            this._currentSource.MediaPropertiesUpdated -= this.OnCurrentMediaPlaybackInfoChanged;
        }

        this._currentSource = source;

        if (!this._disposed && source is not null)
        {
            source.PlaybackInfoChanged += this.OnCurrentMediaPlaybackInfoChanged;
            source.MediaPropertiesUpdated += this.OnCurrentMediaPlaybackInfoChanged;
        }

        this._pendingCurrentMediaSource = source;
        return true;
    }

    private void QueueNotificationsUnderLock(NotificationFlags notifications)
    {
        if (this._disposed || notifications == NotificationFlags.None)
        {
            return;
        }

        this._pendingNotifications |= notifications;
        this._notificationAction.Invoke();
    }

    private bool SetIsLoadingUnderLock(bool value)
    {
        if (this._isLoading == value)
        {
            return false;
        }

        this._isLoading = value;
        return true;
    }

    private void NotifyCurrentSourceChanged()
    {
        this.PropertyChanged?.Invoke(this, new(nameof(this.CurrentSource)));
    }

    private void NotifyIsLoadingChanged()
    {
        this.PropertyChanged?.Invoke(this, new(nameof(this.IsLoading)));
        this.LoadingStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LogSessionDiagnosticOnce(string key, string message)
    {
        if (this._loggedSessionDiagnosticKeys.Count >= MaxLoggedSessionDiagnostics
            || !this._loggedSessionDiagnosticKeys.Add(key))
        {
            return;
        }

        Logger.LogWarning(message);
    }

    private void Refresh()
    {
        lock (this._lifecycleLock)
        {
            if (!this._disposed && !GsmtcOperationGate.IsCircuitOpen)
            {
                this._refreshAction.Invoke();
            }
        }
    }

    private MediaSource[] RefreshCore()
    {
        GsmtcOperationGate.VerifyAccess();

        GlobalSystemMediaTransportControlsSessionManager? manager;
        Dictionary<string, MediaSource> existingSources;
        lock (this._lifecycleLock)
        {
            if (this._disposed)
            {
                return [];
            }

            manager = this._sessionManager;
            existingSources = new(this._sourcesByAppId, StringComparer.Ordinal);
            if (this.SetIsLoadingUnderLock(true))
            {
                this.QueueNotificationsUnderLock(NotificationFlags.IsLoadingChanged);
            }
        }

        var preparedSources = new Dictionary<string, MediaSource>(StringComparer.Ordinal);
        var preparedSourcesCommitted = false;
        List<MediaSource> sourcesToUpdate = [];
        try
        {
            var sessions = manager?.GetSessions() ?? [];

            string? currentAppId = null;
            try
            {
                currentAppId = manager?.GetCurrentSession()?.SourceAppUserModelId;
            }
            catch (Exception ex) when (GsmtcErrors.IndicatesStaleSession(ex))
            {
                this.LogSessionDiagnosticOnce(
                    "stale-current-session",
                    "GSMTC current session is no longer valid; keeping no current source this refresh.");
            }

            var sessionsByAppId = new Dictionary<string, GlobalSystemMediaTransportControlsSession>(StringComparer.Ordinal);

            foreach (var session in sessions)
            {
                string appId;
                try
                {
                    appId = session.SourceAppUserModelId;
                }
                catch (Exception ex) when (GsmtcErrors.IndicatesStaleSession(ex))
                {
                    this.LogSessionDiagnosticOnce(
                        "stale-session-enumeration",
                        "GSMTC returned a session that is no longer valid; skipping it.");
                    continue;
                }

                if (!sessionsByAppId.TryAdd(appId, session))
                {
                    this.LogSessionDiagnosticOnce(
                        $"duplicate:{appId}",
                        $"GSMTC returned more than one session for AUMID {appId}; using the first session.");
                }
            }

            // WinRT event registration can block, so prepare all session proxies without
            // holding the managed lifecycle lock.
            foreach (var (appId, session) in sessionsByAppId)
            {
                try
                {
                    if (existingSources.TryGetValue(appId, out var existingSource))
                    {
                        if (existingSource.UpdateSessionUnderGate(session, appId))
                        {
                            sourcesToUpdate.Add(existingSource);
                        }

                        continue;
                    }

                    var newSource = new MediaSource(session, appId);
                    newSource.HookSessionUnderGate();
                    newSource.SessionInvalidated += this.OnSourceSessionInvalidated;
                    preparedSources.Add(appId, newSource);
                    sourcesToUpdate.Add(newSource);
                }
                catch (Exception ex) when (GsmtcErrors.IndicatesStaleSession(ex))
                {
                    this.LogSessionDiagnosticOnce(
                        $"stale-prepare:{appId}",
                        $"Session for AUMID {appId} became invalid during refresh; skipping it.");
                }
            }

            List<MediaSource> sourcesToDispose = [];
            lock (this._lifecycleLock)
            {
                if (this._disposed)
                {
                    return [];
                }

                var changed = false;

                // Remove sources that no longer exist
                var toRemove = this._sourcesByAppId.Keys
                    .Where(appId => !sessionsByAppId.ContainsKey(appId))
                    .ToList();
                foreach (var appId in toRemove)
                {
                    if (this._sourcesByAppId.Remove(appId, out var source))
                    {
                        this._sources.Remove(source);
                        sourcesToDispose.Add(source);
                        changed = true;
                    }
                }

                foreach (var (appId, newSource) in preparedSources)
                {
                    this._sources.Add(newSource);
                    this._sourcesByAppId.Add(appId, newSource);
                    changed = true;
                }

                preparedSourcesCommitted = true;

                var notifications = changed
                    ? NotificationFlags.MediaSourcesChanged
                    : NotificationFlags.None;

                var currentSource = currentAppId is not null
                    ? this._sourcesByAppId.GetValueOrDefault(currentAppId)
                    : null;
                if (this.SetCurrentSourceUnderLock(currentSource))
                {
                    notifications |= NotificationFlags.CurrentMediaSourceChanged
                        | NotificationFlags.CurrentMediaPlaybackChanged;
                }

                this.QueueNotificationsUnderLock(notifications);
            }

            DisposeSources(sourcesToDispose);
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation
            return [];
        }
        finally
        {
            if (!preparedSourcesCommitted)
            {
                DisposeSources(preparedSources.Values);
            }

            lock (this._lifecycleLock)
            {
                if (!this._disposed && this.SetIsLoadingUnderLock(false))
                {
                    this.QueueNotificationsUnderLock(NotificationFlags.IsLoadingChanged);
                }
            }
        }

        return [.. sourcesToUpdate];
    }

    private async Task RefreshCoreAsync()
    {
        try
        {
            var sourcesToUpdate = await GsmtcOperationGate.RunAsync(this.RefreshCore, this._disposeToken);
            Interlocked.Exchange(ref this._consecutiveRefreshFailures, 0);
            foreach (var source in sourcesToUpdate)
            {
                source.Update();
            }
        }
        catch (OperationCanceledException) when (this._disposeToken.IsCancellationRequested)
        {
        }
        catch (GsmtcCircuitOpenException)
        {
            // Refreshing is pointless until the process restarts.
        }
        catch (Exception ex)
        {
            // GSMTC fails transiently (typically E_BOUNDS) while sessions
            // churn. Retry a few times, then wait for the next
            // sessions-changed event instead of looping on a broken manager.
            var failures = Interlocked.Increment(ref this._consecutiveRefreshFailures);
            if (failures > MaxConsecutiveRefreshFailures)
            {
                Logger.LogError(ex);
                return;
            }

            Logger.LogWarning($"Media source refresh failed (attempt {failures}); retrying: {ex.Message}");
            try
            {
                await Task.Delay(RefreshRetryDelay, this._disposeToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            this.Refresh();
        }
    }

    private void OnSourceSessionInvalidated(object? sender, EventArgs e)
    {
        this.Refresh();
    }

    private void SessionManagerOnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        this.Refresh();
    }

    private void SessionManagerOnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        this.Refresh();
    }

    private async Task UnhookSessionManagerAsync(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        try
        {
            await GsmtcOperationGate.RunAsync(
                () =>
                {
                    manager.SessionsChanged -= this.SessionManagerOnSessionsChanged;
                    manager.CurrentSessionChanged -= this.SessionManagerOnCurrentSessionChanged;
                });
        }
        catch (GsmtcCircuitOpenException)
        {
            // The process must be restarted before native cleanup is safe.
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Could not unsubscribe from the GSMTC manager: {ex.Message}");
        }
    }

    private void FirePendingNotifications()
    {
        NotificationFlags notifications;
        MediaSource? currentSource;
        lock (this._lifecycleLock)
        {
            if (this._disposed)
            {
                return;
            }

            notifications = this._pendingNotifications;
            currentSource = this._pendingCurrentMediaSource;
            this._pendingNotifications = NotificationFlags.None;
            this._pendingCurrentMediaSource = null;
        }

        if ((notifications & NotificationFlags.IsLoadingChanged) != 0)
        {
            this.NotifyIsLoadingChanged();
        }

        if ((notifications & NotificationFlags.MediaSourcesChanged) != 0)
        {
            this.MediaSourcesChanged?.Invoke(this, EventArgs.Empty);
        }

        if ((notifications & NotificationFlags.CurrentMediaSourceChanged) != 0)
        {
            this.NotifyCurrentSourceChanged();
            this.CurrentMediaSourceChanged?.Invoke(this, currentSource);
        }

        if ((notifications & NotificationFlags.CurrentMediaPlaybackChanged) != 0)
        {
            this.CurrentMediaPlaybackChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void DisposeSources(IEnumerable<MediaSource> sources)
    {
        foreach (var source in sources)
        {
            source.SessionInvalidated -= this.OnSourceSessionInvalidated;
            try
            {
                source.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }
        }
    }
}