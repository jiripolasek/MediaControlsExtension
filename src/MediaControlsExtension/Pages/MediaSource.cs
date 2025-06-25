// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace JPSoftworks.MediaControlsExtension.Pages;

internal sealed partial class MediaSource : BaseObservable, IDisposable
{
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _lastTask;

    public string Name
    {
        get;
        set
        {
            field = value;
            this.OnPropertyChanged(nameof(this.Name));
        }
    } = "";

    public string Artist
    {
        get;
        set
        {
            field = value;
            this.OnPropertyChanged(nameof(this.Artist));
        }
    } = "";

    public bool IsPlaying
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                this.OnPropertyChanged(nameof(this.IsPlaying));
            }
        }
    }

    public string? ApplicationIconPath
    {
        get;
        private set
        {
            if (field != value)
            {
                field = value;
                this.OnPropertyChanged(nameof(this.ApplicationIconPath));
            }
        }
    }

    public string? ApplicationName
    {
        get;
        private set
        {
            if (field != value)
            {
                field = value;
                this.OnPropertyChanged(nameof(this.ApplicationName));
            }
        }
    }

    public IRandomAccessStream? Thumbnail { get; private set; }

    public IAppInfo? AppInfo { get; private set; }

    public MediaPlaybackType PlaybackType
    {
        get;
        private set
        {
            field = value;
            this.OnPropertyChanged(nameof(this.PlaybackType));
        }
    }

    public GlobalSystemMediaTransportControlsSession Session { get; }

    public MediaSource(GlobalSystemMediaTransportControlsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        this.Session = session;
        this.Session.MediaPropertiesChanged += this.SessionOnMediaPropertiesChanged;
        this.Session.PlaybackInfoChanged += this.SessionOnPlaybackInfoChanged;
        this.TriggerUpdate();
    }

    public void Dispose()
    {
        try
        {
            this.Session.MediaPropertiesChanged -= this.SessionOnMediaPropertiesChanged;
            this.Session.PlaybackInfoChanged -= this.SessionOnPlaybackInfoChanged;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex.ToString());
        }

        this._cancellationTokenSource?.Dispose();
        this.Thumbnail?.Dispose();
    }

    private void SessionOnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args)
    {
        this.TriggerUpdate();
    }

    private void SessionOnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args)
    {
        this.TriggerUpdate();
    }

    private void TriggerUpdate()
    {
        try
        {
            this._cancellationTokenSource?.Cancel();
            this._cancellationTokenSource = new CancellationTokenSource();
            this._lastTask = this.UpdatePropertiesFromSession(this.Session, this._cancellationTokenSource.Token);
        }
        catch (Exception ex)
        {
            Logger.LogError("Exception @ TriggerUpdate", ex);
            throw;
        }
    }

    private async Task UpdatePropertiesFromSession(
        GlobalSystemMediaTransportControlsSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            this.AppInfo = UpdateAppDisplayInfo(session);
            this.ApplicationName = this.AppInfo.DisplayName ?? "";
            this.ApplicationIconPath = this.AppInfo.IconPath;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }

        try
        {
            this.IsPlaying = session.GetPlaybackInfo()?.PlaybackStatus ==
                             GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var mediaProperties = await session.TryGetMediaPropertiesAsync()!;
            if (mediaProperties != null)
            {
                this.Name = mediaProperties.Title ?? string.Empty;
                this.Artist = mediaProperties.Artist ?? string.Empty;
                this.PlaybackType = mediaProperties.PlaybackType ?? MediaPlaybackType.Unknown;
                if (mediaProperties.Thumbnail != null)
                {
                    this.Thumbnail = await mediaProperties.Thumbnail.OpenReadAsync()!.AsTask(cancellationToken)!;
                }
            }
            else
            {
                this.Name = string.Empty;
                this.Artist = string.Empty;
                this.PlaybackType = MediaPlaybackType.Unknown;
                this.Thumbnail = null;
            }
        }
        catch (OperationCanceledException)
        {
            // ignore this exception, it is expected when the task is cancelled
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }
    }

    private static IAppInfo UpdateAppDisplayInfo(GlobalSystemMediaTransportControlsSession session)
    {
        if (string.IsNullOrWhiteSpace(session.SourceAppUserModelId))
        {
            return EmptyAppInfo.Instance;
        }

        var appInfo = ModernAppHelper.Get(session.SourceAppUserModelId);
        if (appInfo != null)
        {
            var appDisplayInfo = appInfo.DisplayInfo;
            if (appDisplayInfo != null)
            {
                return new ModernAppInfo(appInfo, PackageIconHelper.GetBestIconPath(session.SourceAppUserModelId));
            }
        }

        var desktopApp = DesktopAppHelper.GetExecutable(session.SourceAppUserModelId);
        if (desktopApp is not null)
        {
            return desktopApp;
        }

        return EmptyAppInfo.Instance;
    }
}