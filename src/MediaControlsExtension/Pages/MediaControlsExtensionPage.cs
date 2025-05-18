// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Collections.Immutable;
using JPSoftworks.MediaControlsExtension.Commands;
using JPSoftworks.MediaControlsExtension.Resources;
using JPSoftworks.MediaControlsExtension.Threading;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.Pages;

internal sealed partial class MediaControlsExtensionPage : ListPage, IDisposable
{
    private readonly System.Timers.Timer _throttleTimer;
    private readonly SettingsManager _settingsManager;
    private readonly Lock _resultsLock = new();
    private CancellationTokenSource? _cancellationTokenSource;
    private Task<ImmutableArray<MediaSource>>? _currentUpdateTask;

    private bool _isInitialized;
    private ImmutableArray<MediaSource> _results = [];
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private IListItem[] _commandsForNoSession = [];
    private ListItem? _playPauseCommand;
    private ListItem? _nextTrackCommand;
    private ListItem? _prevTrackCommand;
    private ListItem? _muteCommand;

    public MediaControlsExtensionPage(SettingsManager settingsManager)
    {
        ArgumentNullException.ThrowIfNull(settingsManager);

        this._settingsManager = settingsManager;
        this.Icon = Icons.MainIcon;
        this.Title = Strings.Name!;
        this.Name = Strings.Open!;
        this.Id = "com.jpsoftworks.cmdpal.mediacontrols";

        this._throttleTimer = new System.Timers.Timer(100);
        this._throttleTimer.Enabled = false;
        this._throttleTimer.Elapsed += (_, _) =>
        {
            this._throttleTimer.Stop();
            this.RefreshCore();
        };
    }

    public void Dispose()
    {
        this._cancellationTokenSource?.Dispose();
        this._currentUpdateTask?.Dispose();

        foreach (var mediaSource in this._results)
        {
            mediaSource.Dispose();
        }

        if (this._sessionManager != null)
        {
            this._sessionManager.SessionsChanged -= this.SessionManagerOnSessionsChanged;
            this._sessionManager.CurrentSessionChanged -= this.SessionManagerOnCurrentSessionChanged;
        }
    }

    public override IListItem[] GetItems()
    {
        try
        {
            return this.GetItemsUnsafe();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
            return [];
        }
    }

    private IListItem[] GetItemsUnsafe()
    {
        if (!this._isInitialized)
        {
            if (!this.IsLoading)
            {
                this.IsLoading = true;
                _ = this.InitializeAsync();
            }

            return this.GetGlobalCommands();
        }

        return
        [
            ..this.GetGlobalCommands(),
            ..this._results.Select(mediaSource =>
                new MediaSourceListItem(mediaSource, this._settingsManager,
                    new PlayPauseSessionMediaCommand(this._sessionManager!, mediaSource.Session)))
        ];
    }

    private IListItem[] GetGlobalCommands()
    {
        var currentSession = this._sessionManager?.GetCurrentSession();

        if (currentSession == null)
        {
            return this._commandsForNoSession;
        }
        else
        {
            List<IListItem> items =
            [
                this._playPauseCommand!
            ];
            if (currentSession.GetPlaybackInfo()?.Controls?.IsNextEnabled == true)
            {
                items.Add(this._nextTrackCommand!);
            }
            if (currentSession.GetPlaybackInfo()?.Controls?.IsPreviousEnabled == true)
            {
                items.Add(this._prevTrackCommand!);
            }
            items.Add(this._muteCommand!);

            return [.. items];
        }
    }

    private async Task InitializeAsync()
    {
        Logger.LogInformation("InitializeAsync started...");

        try
        {
            this._sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync()!;
            this._sessionManager.SessionsChanged += this.SessionManagerOnSessionsChanged;
            this._sessionManager.CurrentSessionChanged += this.SessionManagerOnCurrentSessionChanged;

            this._playPauseCommand = new ListItem(new PlayPauseMediaCommand(this._sessionManager!))
            {
                Title = Strings.TogglePlayPause!, Subtitle = Strings.TogglePlayPause_Comments!
            };
            this._nextTrackCommand = new ListItem(new NextTrackInvokableMediaCommand(this._sessionManager!));
            this._prevTrackCommand = new ListItem(new PreviousTrackInvokableMediaCommand(this._sessionManager!));
            this._muteCommand = new ListItem(new ToggleMuteMediaInvokableCommand());
            this._commandsForNoSession = [this._muteCommand];
            this._isInitialized = true;

            this.RefreshCore();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
            throw;
        }
    }

    private void SessionManagerOnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        this.Refresh();
    }

    private void SessionManagerOnSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args)
    {
        this.Refresh();
    }


    private void Refresh()
    {
        if (this._isInitialized)
        {
            this._throttleTimer.Start();
        }
    }

    private void RefreshCore()
    {
        this._cancellationTokenSource?.Cancel();
        this._cancellationTokenSource = new CancellationTokenSource();

        this.IsLoading = true;

        this._currentUpdateTask = this.UpdateMediaSourcesAsync(this._cancellationTokenSource.Token);
        _ = this.ProcessSearchResultsAsync(this._currentUpdateTask);
    }

    private async Task ProcessSearchResultsAsync(Task<ImmutableArray<MediaSource>> searchTask)
    {
        try
        {
            var newMediaSessions = await searchTask;

            if (searchTask != this._currentUpdateTask)
            {
                return;
            }

            var oldResults = this._results;
            lock (this._resultsLock)
            {
                this._results = newMediaSessions;
            }

            foreach (var mediaSource in oldResults)
            {
                mediaSource.Dispose();
            }

            this.IsLoading = false;
            this.RaiseItemsChanged();
            this._currentUpdateTask = null;
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation
        }
    }


    private Task<ImmutableArray<MediaSource>> UpdateMediaSourcesAsync(CancellationToken cancellationToken)
    {
        return ComThread.BeginInvoke(() =>
        {
            try
            {
                var result = this.GetMediaSessionSources();
                return result;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }

            return [];
        }, cancellationToken);
    }


    private ImmutableArray<MediaSource> GetMediaSessionSources()
    {
        return [
            ..this._sessionManager!
                .GetSessions()!
                .Select(static session => new MediaSource(session))
        ];
    }
}