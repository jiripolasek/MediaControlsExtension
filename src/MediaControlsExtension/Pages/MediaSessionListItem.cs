// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Text;
using Windows.Media;

namespace JPSoftworks.MediaControlsExtension.Pages;

internal sealed partial class MediaSessionListItem : ListItemBase, IDisposable
{
    private static readonly Tag PlayingTag = new() { Text = Strings.Tags_Playing!, Icon = Icons.PlaySolid, Foreground = new(true, new(0, 255, 0, 128)), Background = new(true, new(0, 255, 00, 40)) };

    private readonly SettingsManager _settingsManager;
    private readonly IIconService _iconService;
    private readonly IconSurface _iconSurface;
    private readonly ThrottledAction _throttledAction;
    private readonly OptimisticPlaybackCommand _command;
    private readonly BringAssociatedAppToFrontCommand _switchToApplicationCommand;
    private readonly ICommand _nextTrackCommand;
    private readonly ICommand _previousTrackCommand;
#if FF_ENABLE_FULL_METADATA_PAGE
    private MediaMetadataPage? _metadataPage;
#endif
    private readonly Lock _updateLock = new();
    private MediaSessionViewModel? _viewModel;
    private readonly MediaSessionId _sessionId;
    private readonly bool _asBand;

    private NiceIconInfo? _lastIcon;
    private MediaDetails? _mediaDetails;
    private TagPresentation? _tagPresentation;
    private int _detailsRequested;
    private int _disposed;

    internal MediaSessionId SessionId => this._sessionId;

    public override IDetails? Details
    {
        get
        {
            var details = Volatile.Read(ref this._mediaDetails);
            var viewModel = Volatile.Read(ref this._viewModel);
            if (details is null &&
                Volatile.Read(ref this._disposed) == 0 &&
                viewModel is not null)
            {
                Interlocked.Exchange(ref this._detailsRequested, 1);
                viewModel.RequestArtwork();
                var newDetails = this.CreateDetails(viewModel);
                details = Interlocked.CompareExchange(
                    ref this._mediaDetails,
                    newDetails,
                    null) ?? newDetails;
            }

            return details?.Details;
        }
        set
        {
        }
    }

    public MediaSessionListItem(
        IMediaService mediaService,
        MediaSessionViewModelCache viewModels,
        MediaMetadataPageCache metadataPages,
        MediaSessionViewModel viewModel,
        SettingsManager settingsManager,
        MediaCommandResultFactory resultFactory,
        IIconService iconService,
        bool asBand) : base(new NoOpCommand())
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(viewModels);
        ArgumentNullException.ThrowIfNull(metadataPages);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(settingsManager);
        ArgumentNullException.ThrowIfNull(resultFactory);
        ArgumentNullException.ThrowIfNull(iconService);

        this._viewModel = viewModel;
        this._sessionId = viewModel.Session.Id;
        this._settingsManager = settingsManager;
        this._iconService = iconService;
        this._iconSurface = asBand
            ? IconSurface.Dock
            : IconSurface.CommandPalette;
        this._throttledAction = new(
            100,
            $"MediaSessionListItem[{viewModel.Session.Id.Value}].Update",
            () =>
            {
                if (Volatile.Read(ref this._viewModel) is { } currentViewModel)
                {
                    this.Update(currentViewModel);
                }
            });

        this._viewModel.Changed += this.ViewModelOnChanged;
        this._settingsManager.Settings.SettingsChanged += this.SettingsOnSettingsChanged;

        this.Title = Strings.Command_PlayPause!;
        this.Icon = iconService.GetIcon(ThemedIcon.PlayPause, this._iconSurface);
        this._asBand = asBand;

        this.Command = this._command = new(
            mediaService,
            resultFactory,
            iconService,
            this._iconSurface);
        this._switchToApplicationCommand = new(
            mediaService,
            viewModels,
            viewModel.Session.Id);
        this._nextTrackCommand = new NextTrackInvokableSpecificMediaCommand(
            mediaService,
            viewModel.Session,
            resultFactory)
        {
            Icon = iconService.GetIcon(ThemedIcon.SkipNext, this._iconSurface),
        };
        this._previousTrackCommand = new PreviousTrackInvokableSpecificMediaCommand(
            mediaService,
            viewModel.Session,
            resultFactory)
        {
            Icon = iconService.GetIcon(ThemedIcon.SkipPrevious, this._iconSurface),
        };
        var toggleRepeatCommand = new ToggleRepeatSpecificMediaCommand(
            mediaService,
            viewModel.Session,
            resultFactory);
        var toggleShuffleCommand = new ToggleShuffleSpecificMediaCommand(
            mediaService,
            viewModel.Session,
            resultFactory);
#if FF_ENABLE_FULL_METADATA_PAGE
        this._metadataPage = metadataPages.GetOrCreate(viewModel);
#endif

        this.MoreCommands =
        [
            new CommandContextItem(this._switchToApplicationCommand) { RequestedShortcut = Chords.SwitchToApplication, Icon = Icons.SwitchApps },
#if FF_ENABLE_FULL_METADATA_PAGE
            new CommandContextItem(this._metadataPage) { RequestedShortcut = Chords.ViewMetadata, Icon = Icons.Metadata },
#endif
            new Separator(),
            new CommandContextItem(this._nextTrackCommand) { RequestedShortcut = Chords.NextTrack, Icon = Icons.NextTrackOutline },
            new CommandContextItem(this._previousTrackCommand) { RequestedShortcut = Chords.PreviousTrack, Icon = Icons.PreviousTrackOutline },
            new Separator(),
            new CommandContextItem(toggleRepeatCommand) { RequestedShortcut = Chords.ToggleRepeat, Icon = Icons.ToggleRepeat },
            new CommandContextItem(toggleShuffleCommand) { RequestedShortcut = Chords.ToggleShuffle, Icon = Icons.ToggleShuffle },
        ];

        this.Update(viewModel);
    }

    private void Update(MediaSessionViewModel viewModel)
    {
        var detailsChanged = false;
        lock (this._updateLock)
        {
            if (Volatile.Read(ref this._disposed) != 0)
            {
                return;
            }

            try
            {
                detailsChanged = this.UpdateCore(viewModel);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }
        }

        if (detailsChanged)
        {
            this.OnPropertyChanged(nameof(this.Details));
        }
    }

    private bool UpdateCore(MediaSessionViewModel viewModel)
    {
        var properties = viewModel.MediaProperties;
        var playback = viewModel.PlaybackInfo;
        var isPlaying = playback.EffectiveState == MediaPlaybackState.Playing;

        this.Title = (isPlaying && !this._asBand ? "▶️ " : string.Empty) + properties.Title;
        this.Subtitle = BuildSubtitle(viewModel);
        this._command.UpdatePresentation(viewModel.Session);
        this.UpdateNavigationCommandIcons();
        var detailsChanged = this.UpdateDetails(viewModel);
        this.UpdateTags(viewModel);

        if (this._settingsManager.ShowThumbnails)
        {
            viewModel.RequestArtwork();
        }

        var icon = MediaSessionIcons.CreateDisplayIcon(
            viewModel,
            this._settingsManager.ShowThumbnails);
        if (this._lastIcon != icon)
        {
            this._lastIcon = icon;
            this.Icon = icon.IconInfo;
        }

        return detailsChanged;
    }

    private static string BuildSubtitle(MediaSessionViewModel viewModel)
    {
        var subtitleBuilder = new StringBuilder();
        subtitleBuilder.AppendWhenNotEmpty(" • ", viewModel.MediaProperties.Artist);
        subtitleBuilder.AppendWhenNotEmpty(" • ", viewModel.ApplicationName);

#if DEBUG
        subtitleBuilder.AppendWhenNotEmpty(
            " • ",
            viewModel.MediaProperties.Application.ApplicationId);
        subtitleBuilder.AppendWhenNotEmpty(
            " • ",
            Path.GetFileName(viewModel.ApplicationIconPath));
#endif

        return subtitleBuilder.ToString();
    }

    private bool UpdateDetails(MediaSessionViewModel viewModel)
    {
        if (Volatile.Read(ref this._detailsRequested) == 0)
        {
            return false;
        }

        var details = Volatile.Read(ref this._mediaDetails);
        if (details is not null && details.Represents(viewModel))
        {
            return false;
        }

        Volatile.Write(ref this._mediaDetails, this.CreateDetails(viewModel));
        return true;
    }

    private MediaDetails CreateDetails(MediaSessionViewModel viewModel)
    {
        ICommand? viewMetadataCommand = null;
#if FF_ENABLE_FULL_METADATA_PAGE
        viewMetadataCommand = Volatile.Read(ref this._metadataPage);
#endif
        return new(
            this._previousTrackCommand,
            this._command,
            this._nextTrackCommand,
            this._switchToApplicationCommand,
            viewModel,
            viewMetadataCommand);
    }

    private void UpdateTags(MediaSessionViewModel viewModel)
    {
        var showApplicationTag = this._settingsManager.ShowThumbnails;
        var presentation = new TagPresentation(
            viewModel.PlaybackInfo.EffectiveState == MediaPlaybackState.Playing,
            showApplicationTag,
            showApplicationTag ? viewModel.ApplicationName : string.Empty,
            showApplicationTag ? viewModel.ApplicationIconPath : null,
            showApplicationTag ? viewModel.PlaybackType : MediaPlaybackType.Unknown);
        if (this._tagPresentation == presentation)
        {
            return;
        }

        this._tagPresentation = presentation;
        this.Tags = BuildTags(presentation);
    }

    private static ITag[] BuildTags(TagPresentation presentation)
    {
        var tags = new List<ITag>(2);
        if (presentation.IsPlaying)
        {
            tags.Add(PlayingTag);
        }

        if (presentation.ShowApplicationTag)
        {
            tags.Add(new Tag
            {
                Text = presentation.ApplicationName,
                Icon = MediaSessionIcons.GetFallbackIcon(
                    presentation.ApplicationIconPath,
                    presentation.PlaybackType),
            });
        }

        return [.. tags];
    }

    private readonly record struct TagPresentation(
        bool IsPlaying,
        bool ShowApplicationTag,
        string ApplicationName,
        string? ApplicationIconPath,
        MediaPlaybackType PlaybackType);

    private void UpdateNavigationCommandIcons()
    {
        if (this._nextTrackCommand is Command nextTrackCommand)
        {
            nextTrackCommand.UpdateIcon(this._iconService.GetIcon(
                ThemedIcon.SkipNext,
                this._iconSurface));
        }

        if (this._previousTrackCommand is Command previousTrackCommand)
        {
            previousTrackCommand.UpdateIcon(this._iconService.GetIcon(
                ThemedIcon.SkipPrevious,
                this._iconSurface));
        }
    }

    private bool Equals(MediaSessionListItem other) =>
        this._sessionId == other._sessionId;

    public override bool Equals(object? obj) =>
        ReferenceEquals(this, obj) ||
        obj is MediaSessionListItem other && this.Equals(other);

    public override int GetHashCode() => this._sessionId.GetHashCode();

    private void SettingsOnSettingsChanged(object sender, Settings args) => this.ScheduleUpdate();

    private void ViewModelOnChanged(object? sender, EventArgs args) => this.ScheduleUpdate();

    private void ScheduleUpdate()
    {
        if (Volatile.Read(ref this._disposed) != 0)
        {
            return;
        }

        try
        {
            this._throttledAction.Invoke();
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref this._disposed) != 0)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref this._disposed, 1) != 0)
        {
            return;
        }

        try
        {
            this._throttledAction.Dispose();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }

        this._settingsManager.Settings.SettingsChanged -= this.SettingsOnSettingsChanged;
        var viewModel = Interlocked.Exchange(ref this._viewModel, null);
        if (viewModel is not null)
        {
            viewModel.Changed -= this.ViewModelOnChanged;
        }

        this._command.UpdatePresentation(null);
        Interlocked.Exchange(ref this._mediaDetails, null);
        this._lastIcon = null;
        this.Icon = Icons.Unknown;
        this.Tags = [];
        this.MoreCommands = [];
#if FF_ENABLE_FULL_METADATA_PAGE
        Interlocked.Exchange(ref this._metadataPage, null);
#endif
    }
}
