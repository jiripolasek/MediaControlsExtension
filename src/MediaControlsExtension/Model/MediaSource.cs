// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Runtime.CompilerServices;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace JPSoftworks.MediaControlsExtension.Model;

internal sealed partial class MediaSource : BaseObservable, IDisposable
{
    internal sealed record MediaUpdateRequest(bool UpdatePlayback, bool UpdateMediaProperties);

    private readonly CoalescingAsyncLoader<MediaUpdateRequest, object> _mediaUpdateLoader;
    private readonly CoalescingAsyncLoader<IRandomAccessStreamReference?, ThumbnailInfo> _thumbnailLoader;
    private bool _disposed;

    public event EventHandler? MediaPropertiesUpdated;
    public event EventHandler? PlaybackInfoChanged;
    public event EventHandler? ThumbnailChanged;

    public bool HasProperties
    {
        get;
        private set => this.SetField(ref field, value);
    }

    public string Name
    {
        get;
        set => this.SetField(ref field, value);
    } = "";

    public string Artist
    {
        get;
        set => this.SetField(ref field, value);
    } = "";

    public bool IsPlaying
    {
        get;
        set => this.SetField(ref field, value);
    }

    public string? ApplicationIconPath
    {
        get;
        private set => this.SetField(ref field, value);
    }

    public string? ApplicationName
    {
        get;
        private set => this.SetField(ref field, value);
    }

    public ThumbnailInfo? ThumbnailInfo
    {
        get;
        private set => this.SetField(ref field, value);
    }

    public IAppInfo? AppInfo
    {
        get;
        private set => this.SetField(ref field, value);
    }

    public MediaPlaybackType PlaybackType
    {
        get;
        private set => this.SetField(ref field, value);
    }

    public string SessionId { get; }

    public GlobalSystemMediaTransportControlsSession Session { get; private set; }

    public void UpdateSession(GlobalSystemMediaTransportControlsSession session)
    {
        this.UnhookSession();
        this.Session = session;
        this.HookSession();
    }

    public void UpdateSessionWhenDifferent(GlobalSystemMediaTransportControlsSession session)
    {
        if(session == this.Session)
        {
            return;
        }

        this.UnhookSession();
        this.Session = session;
        this.HookSession();
    }

    private bool _eventsSubscribed;

    private void HookSession()
    {
        try
        {
            this.Session.MediaPropertiesChanged += this.SessionOnMediaPropertiesChanged;
            this.Session.PlaybackInfoChanged += this.SessionOnPlaybackInfoChanged;
            this._eventsSubscribed = true;
        }
        catch (Exception ex)
        {
            this._eventsSubscribed = false;
            Logger.LogWarning($"Could not subscribe to events for {this.SessionId}: {ex.Message}");
        }
    }

    private void UnhookSession()
    {
        if (this._eventsSubscribed)
        {
            try
            {
                this.Session.MediaPropertiesChanged -= this.SessionOnMediaPropertiesChanged;
                this.Session.PlaybackInfoChanged -= this.SessionOnPlaybackInfoChanged;
            }
            catch
            {
                // Ignore errors during unsubscription
            }
        }

        this._eventsSubscribed = false;
    }

    public MediaSource(GlobalSystemMediaTransportControlsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        this.Session = session;
        this.SessionId = session.SourceAppUserModelId;
        this.UpdateSession(this.Session);

        this._thumbnailLoader = new(
            static (reference, token) => ThumbnailLoader.LoadAsync(reference, 20, 20, token),
            info =>
            {
                if (info != this.ThumbnailInfo)
                {
                    this.ThumbnailInfo = info;
                    this.ThumbnailChanged?.Invoke(this, EventArgs.Empty);
                }
            },
            _ => { }
        );

        this._mediaUpdateLoader = new(
            async (request, token) =>
            {
                await this.UpdatePropertiesFromSession(
                    this.Session, 
                    request.UpdatePlayback, 
                    request.UpdateMediaProperties, 
                    token);
                return null;
            },
            _ => {  },
            _ => {  }
        );

        this.TriggerUpdate(true, true);
    }

    public void Dispose()
    {
        if (this._disposed)
        {
            return;
        }

        this._disposed = true;
        
        this.UnhookSession();

        this._thumbnailLoader.Dispose();
        this._mediaUpdateLoader.Dispose();

        this.ThumbnailInfo?.Stream?.Dispose();
        this.ThumbnailInfo = null;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        this.OnPropertyChanged(propertyName!);
    }

    private void SessionOnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args)
    {
        this.TriggerUpdate(true, false);
    }

    private void SessionOnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args)
    {
        this.TriggerUpdate(true, true);
    }

    private void TriggerUpdate(bool playback, bool mediaProps)
    {
        if (this._disposed)
        {
            return;
        }

        this._mediaUpdateLoader.Schedule(new(playback, mediaProps));
    }

    private void ScheduleThumbnailUpdate(IRandomAccessStreamReference? thumbnailRef)
    {
        this._thumbnailLoader.Schedule(thumbnailRef);
    }

    private async Task UpdatePropertiesFromSession(
        GlobalSystemMediaTransportControlsSession session,
        bool updatePlayback,
        bool updateMediaProperties,
        CancellationToken cancellationToken)
    {
        if (this.AppInfo == null)
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
        }

        try
        {
            this.IsPlaying = session.GetPlaybackInfo()?.PlaybackStatus ==
                             GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            if (updateMediaProperties)
            {
                var mediaProperties = await session.TryGetMediaPropertiesAsync()!;
                if (mediaProperties != null)
                {
                    this.HasProperties = true;
                    this.Name = mediaProperties.Title ?? string.Empty;
                    this.Artist = mediaProperties.Artist ?? string.Empty;
                    this.PlaybackType = mediaProperties.PlaybackType ?? MediaPlaybackType.Unknown;
                    if (mediaProperties.Thumbnail != null)
                    {
                        this.ScheduleThumbnailUpdate(mediaProperties.Thumbnail);
                    }
                }
                else
                {
                    this.HasProperties = false;
                    this.Name = string.Empty;
                    this.Artist = string.Empty;
                    this.PlaybackType = MediaPlaybackType.Unknown;
                    this.ThumbnailInfo = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ignore this exception, it is expected when the task is cancelled
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to update properties for " + session.SourceAppUserModelId, ex);
        }

        if (updatePlayback)
        {
            this.PlaybackInfoChanged?.Invoke(this, EventArgs.Empty);
        }
        if (updateMediaProperties)
        {
            this.MediaPropertiesUpdated?.Invoke(this, EventArgs.Empty);
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

    private bool Equals(MediaSource other)
    {
        return this.SessionId == other.SessionId;
    }

    public override bool Equals(object? obj)
    {
        return ReferenceEquals(this, obj) || (obj is MediaSource other && this.Equals(other));
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(this.SessionId);
    }

    public override string ToString()
    {
        return $"MediaSource: AppId: {this.SessionId}, {nameof(this.Name)}: {this.Name}, {nameof(this.Artist)}: {this.Artist}, {nameof(this.IsPlaying)}: {this.IsPlaying}, {nameof(this.ApplicationIconPath)}: {this.ApplicationIconPath}, {nameof(this.ApplicationName)}: {this.ApplicationName}, {nameof(this.AppInfo)}: {this.AppInfo}, {nameof(this.PlaybackType)}: {this.PlaybackType}, {nameof(this.Session)}: {this.Session}";
    }

    public void Update()
    {
        this.TriggerUpdate(true, true);
    }
}