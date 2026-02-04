// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation.Collections;
using Windows.Media;

namespace JPSoftworks.MediaControlsExtension.Pages;

internal partial class ListItemBase : ListItem
{
    public override string Title
    {
        get => base.Title;
        set
        {
            if (base.Title != value)
            {
                base.Title = value;
            }
        }
    }

    public override string Subtitle
    {
        get => base.Subtitle;
        set
        {
            if (base.Subtitle != value)
            {
                base.Subtitle = value;
            }
        }
    }

    public override ITag[] Tags
    {
        get => base.Tags;
        set
        {
            if (base.Tags != value)
            {
                base.Tags = value;
            }
        }
    }

    protected ListItemBase(ICommand command) : base(command)
    {
    }

    protected ListItemBase(ICommandItem command) : base(command)
    {
    }
}

internal sealed partial class MediaSourceListItem : ListItemBase, IDisposable
{
    private static readonly Tag PlayingTag = new() { Text = Strings.Tags_Playing!, Icon = Icons.PlaySolid, Foreground = new(true, new(0, 255, 0, 128)), Background = new(true, new(0, 255, 00, 40)) };

    private readonly SettingsManager _settingsManager;
    private readonly ThrottledAction _throttledAction;
    private readonly PlayPauseSpecificMediaCommand _command;

    private NiceIconInfo? _lastIcon;
    private MediaSource _mediaSource;
    private bool _disposed;
    private bool _asBand;

    public MediaSourceListItem(
        MediaService mediaService,
        MediaSource mediaSource,
        SettingsManager settingsManager,
        YetAnotherHelper yetAnotherHelper,
        bool asBand) : base(new NoOpCommand())
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(mediaSource);
        ArgumentNullException.ThrowIfNull(settingsManager);
        ArgumentNullException.ThrowIfNull(yetAnotherHelper);

        this._mediaSource = mediaSource;
        this._settingsManager = settingsManager;

        this._mediaSource.PropChanged += this.MediaSourceOnPropChanged;
        this._settingsManager.Settings.SettingsChanged += this.SettingsOnSettingsChanged;

        this._throttledAction = new(100, () => this.Update(this._mediaSource));

        this.Title = Strings.Command_PlayPause!;
        this.Icon = Icons.PlayPause;

        this._asBand = asBand;

        this.Command = this._command = new(mediaService, mediaSource, settingsManager, yetAnotherHelper);
        var f = new FontIconData("x", "y");
        var d = new Details() { Title = "sad" };
        this.Details = d;
        this.MoreCommands =
        [
            new CommandContextItem(new BringAssociatedAppToFrontCommand(mediaSource)) { RequestedShortcut = Chords.SwitchToApplication, Icon = Icons.SwitchApps },
            new MyCommandContextItem(new NextTrackInvokableSpecificMediaCommand(mediaService, mediaSource, yetAnotherHelper)) { RequestedShortcut = Chords.NextTrack, Icon = Icons.NextTrackOutline },
            new MyCommandContextItem(new PreviousTrackInvokableSpecificMediaCommand(mediaService, mediaSource, yetAnotherHelper)) { RequestedShortcut = Chords.PreviousTrack, Icon = Icons.PreviousTrackOutline },
            new MyCommandContextItem(new ToggleRepeatSpecificMediaCommand(mediaService, mediaSource, yetAnotherHelper)) { RequestedShortcut = Chords.ToggleRepeat, Icon = Icons.ToggleRepeat },
            new MyCommandContextItem(new ToggleShuffleSpecificMediaCommand(mediaService, mediaSource, yetAnotherHelper)) { RequestedShortcut = Chords.ToggleShuffle, Icon = Icons.ToggleShuffle },
        ];

        this.Update(this._mediaSource);
    }

    private void Update(MediaSource mediaSource)
    {
        ArgumentNullException.ThrowIfNull(mediaSource);

        try
        {
            this.UpdateCore(mediaSource);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }
    }

    private void UpdateCore(MediaSource mediaSource)
    {
        this.Title = (mediaSource.IsPlaying && !this._asBand ? "▶️ " : "") + mediaSource.Name;
        this.Subtitle = BuildSubtitle(mediaSource);
        this._command.Name = mediaSource.IsPlaying ? Strings.Command_Pause : Strings.Command_Play;
        this.Tags = BuildTags();

        var iconBuildTask = BuildIcon(mediaSource, this._settingsManager.ShowThumbnails);
        if (this._lastIcon != iconBuildTask)
        {
            this._lastIcon = iconBuildTask;
            this.Icon = iconBuildTask.IconInfo;
        }

        return;

        static NiceIconInfo BuildIcon(MediaSource mediaSource, bool showThumbnail)
        {
            if (showThumbnail && mediaSource.ThumbnailInfo?.Stream != null)
            {
                return new(mediaSource.ThumbnailInfo!);
            }

            if (mediaSource.ApplicationIconPath != null)
            {
                return new(mediaSource.ApplicationIconPath);
            }

            return new(GetFallbackIconForPlaybackType(mediaSource.PlaybackType));
        }

        static string BuildSubtitle(MediaSource mediaSource)
        {
            var subtitleBuilder = new StringBuilder();
            subtitleBuilder.AppendWhenNotEmpty(" • ", mediaSource.Artist);
            subtitleBuilder.AppendWhenNotEmpty(" • ", mediaSource.ApplicationName);

#if DEBUG
            subtitleBuilder.AppendWhenNotEmpty(" • ", mediaSource.Session.SourceAppUserModelId ?? "no AUMID");
            subtitleBuilder.AppendWhenNotEmpty(" • ", Path.GetFileName(mediaSource.ApplicationIconPath));
#endif

            return subtitleBuilder.ToString();
        }

        ITag[] BuildTags()
        {
            var tags = new List<ITag>(2);
            if (mediaSource.IsPlaying)
            {
                tags.Add(PlayingTag);
            }

            if (this._settingsManager.ShowThumbnails)
            {
                tags.Add(new Tag { Text = mediaSource.ApplicationName ?? "", Icon = new IconInfo(mediaSource.ApplicationIconPath) });
            }

            return [.. tags];
        }
    }

    private static IconInfo GetFallbackIconForPlaybackType(MediaPlaybackType playbackType)
    {
        return playbackType switch
        {
            MediaPlaybackType.Music => Icons.Music,
            MediaPlaybackType.Video => Icons.Video,
            MediaPlaybackType.Image => Icons.Image,
            _ => Icons.Unknown
        };
    }

    private bool Equals(MediaSourceListItem other)
    {
        return this._mediaSource.Session.SourceAppUserModelId.Equals(other._mediaSource.Session.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj)
    {
        return ReferenceEquals(this, obj) || obj is MediaSourceListItem other && this.Equals(other);
    }

    public override int GetHashCode()
    {
        return this._mediaSource.GetHashCode();
    }

    private void SettingsOnSettingsChanged(object sender, Settings args) => this._throttledAction.Invoke();

    private void MediaSourceOnPropChanged(object sender, IPropChangedEventArgs args) => this._throttledAction.Invoke();

    public void Dispose()
    {
        if (this._disposed)
        {
            return;
        }

        try
        {
            this._throttledAction.Dispose();
            this._settingsManager.Settings.SettingsChanged -= this.SettingsOnSettingsChanged;
            this._mediaSource.PropChanged -= this.MediaSourceOnPropChanged;
            this._mediaSource = null!;
            this._disposed = true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }
    }
}

public partial class MyCommandContextItem : MyCommandItem, ICommandContextItem
{
    public virtual bool IsCritical { get; set; }

    public virtual KeyChord RequestedShortcut { get; set; }

    public MyCommandContextItem(ICommand command)
        : base(command)
    {
    }

    public MyCommandContextItem(
        string title,
        string subtitle = "",
        string name = "",
        Action? action = null,
        ICommandResult? result = null)
    {
        var c = new AnonymousCommand(action);
        if (!string.IsNullOrEmpty(name))
        {
            c.Name = name;
        }

        if (result is not null)
        {
            c.Result = result;
        }

        Command = c;

        Title = title;
        Subtitle = subtitle;
    }
}

public partial class MyCommandItem : BaseObservable, ICommandItem// , IExtendedAttributesProvider
{
    private readonly PropertySet _extendedAttributes = new();

    private ICommand? _command;
    //private WeakEventListener<CommandItem, object, IPropChangedEventArgs>? _commandListener;
    private string _title = string.Empty;

    private DataPackage? _dataPackage;
    private DataPackageView? _dataPackageView;

    public virtual IIconInfo? Icon { get; set; }

    public virtual string Title
    {
        get => !string.IsNullOrEmpty(_title) ? _title : _command?.Name ?? string.Empty;
        set
        {
            var oldTitle = Title;
            _title = value;
            //if (Title != oldTitle)
            //{
            //    OnPropertyChanged();
            //}
        }
    }

    public virtual string Subtitle { get; set; } = string.Empty;

    public virtual ICommand? Command
    {
        get => _command;
        set
        {
            if (EqualityComparer<ICommand?>.Default.Equals(value, _command))
            {
                return;
            }

            var oldTitle = Title;

            //if (_commandListener is not null)
            //{
            //    _commandListener.Detach();
            //    _commandListener = null;
            //}

            _command = value;

            if (value is not null)
            {
                //_commandListener = new(this, OnCommandPropertyChanged, listener => value.PropChanged -= listener.OnEvent);
                //value.PropChanged += _commandListener.OnEvent;
            }

            //OnPropertyChanged();
            if (string.IsNullOrEmpty(_title) && oldTitle != Title)
            {
                OnPropertyChanged(nameof(Title));
            }
        }
    }

    private void OnCommandPropertyChanged(CommandItem instance, object source, IPropChangedEventArgs args)
    {
        // command's name affects Title only if Title wasn't explicitly set
        if (args.PropertyName == nameof(ICommand.Name) && string.IsNullOrEmpty(_title))
        {
            //instance.OnPropertyChanged(nameof(Title));
        }
    }

    public virtual IContextItem[] MoreCommands { get; set; } = [];

    public DataPackage? DataPackage
    {
        get => _dataPackage;
        set
        {
            _dataPackage = value;
            _dataPackageView = null;
            //_extendedAttributes[WellKnownExtensionAttributes.DataPackage] = value?.AsAgile().Get()?.GetView()!;
            OnPropertyChanged(nameof(DataPackage));
            OnPropertyChanged(nameof(DataPackageView));
        }
    }

    public DataPackageView? DataPackageView
    {
        get => _dataPackageView;
        set
        {
            _dataPackage = null;
            _dataPackageView = value;
            //_extendedAttributes[WellKnownExtensionAttributes.DataPackage] = value?.AsAgile().Get()!;
            OnPropertyChanged(nameof(DataPackage));
            OnPropertyChanged(nameof(DataPackageView));
        }
    }

    public MyCommandItem()
        : this(new NoOpCommand())
    {
    }

    public MyCommandItem(ICommand command)
    {
        Command = command;
    }

    public MyCommandItem(ICommandItem other)
    {
        Command = other.Command;
        Subtitle = other.Subtitle;
        Icon = (IconInfo?)other.Icon;
        MoreCommands = other.MoreCommands;
    }

    public MyCommandItem(
        string title,
        string subtitle = "",
        string name = "",
        Action? action = null,
        ICommandResult? result = null)
    {
        var c = new AnonymousCommand(action);
        if (!string.IsNullOrEmpty(name))
        {
            c.Name = name;
        }

        if (result is not null)
        {
            c.Result = result;
        }

        Command = c;

        Title = title;
        Subtitle = subtitle;
    }

    public IDictionary<string, object> GetProperties()
    {
        return _extendedAttributes;
    }
}
