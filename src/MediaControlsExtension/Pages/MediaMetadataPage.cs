// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

namespace JPSoftworks.MediaControlsExtension.Pages;

#if FF_ENABLE_FULL_METADATA_PAGE
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The visibility lifecycle cancels and disposes artwork work when the page unloads.")]
internal partial class MediaMetadataPage : VisibilityAwareContentPage
{
    private readonly Lock _stateLock = new();
    private readonly IMediaService _mediaService;
    private readonly MediaSessionViewModelCache _viewModels;
    private readonly MediaSessionId? _sessionId;
    private readonly OptimisticPlaybackCommand _playPauseAction;
    // Separate content blocks are always stacked vertically. FormContent keeps the
    // artwork and metadata in one Adaptive Card so they can share a column layout.
    // CmdPal's current Adaptive Cards renderer has no width breakpoint contract, so
    // weighted columns let the hero shrink with the host instead of clipping content.
    private readonly FormContent _metadata = new();
    private readonly IContent[] _content;
    private readonly IContextItem _previousCommand;
    private readonly IContextItem _playPauseCommand;
    private readonly IContextItem _nextCommand;
    private readonly IContextItem _shuffleCommand;
    private readonly IContextItem _repeatCommand;
    private readonly IContextItem _switchToApplicationCommand;

    private MediaSessionViewModel? _viewModel;
    private ThumbnailInfo? _artworkThumbnail;
    private CancellationTokenSource? _artworkCancellation;
    private string? _artworkDataUri;
    private MediaMetadataSnapshot? _snapshot;
    private MediaCommandAvailability? _commandAvailability;
    private bool _isLoaded;

    protected MediaMetadataPage(
        IMediaService mediaService,
        MediaSessionViewModelCache viewModels,
        MediaSessionId? sessionId,
        ICommand previousCommand,
        OptimisticPlaybackCommand playPauseCommand,
        ICommand nextCommand,
        ICommand shuffleCommand,
        ICommand repeatCommand,
        ICommand switchToApplicationCommand)
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(viewModels);
        ArgumentNullException.ThrowIfNull(previousCommand);
        ArgumentNullException.ThrowIfNull(playPauseCommand);
        ArgumentNullException.ThrowIfNull(nextCommand);
        ArgumentNullException.ThrowIfNull(shuffleCommand);
        ArgumentNullException.ThrowIfNull(repeatCommand);
        ArgumentNullException.ThrowIfNull(switchToApplicationCommand);

        this._mediaService = mediaService;
        this._viewModels = viewModels;
        this._sessionId = sessionId;
        this._playPauseAction = playPauseCommand;
        this._content = [this._metadata];
        this._previousCommand = new CommandContextItem(previousCommand) { RequestedShortcut = Chords.PreviousTrack, Icon = Icons.PreviousTrackOutline };
        this._playPauseCommand = new CommandContextItem(playPauseCommand) { RequestedShortcut = Chords.PlayPause, Icon = Icons.PlayPause };
        this._nextCommand = new CommandContextItem(nextCommand) { RequestedShortcut = Chords.NextTrack, Icon = Icons.NextTrackOutline };
        this._shuffleCommand = new CommandContextItem(shuffleCommand) { RequestedShortcut = Chords.ToggleShuffle, Icon = Icons.ToggleShuffle };
        this._repeatCommand = new CommandContextItem(repeatCommand) { RequestedShortcut = Chords.ToggleRepeat, Icon = Icons.ToggleRepeat };
        this._switchToApplicationCommand = new CommandContextItem(switchToApplicationCommand) { RequestedShortcut = Chords.SwitchToApplication, Icon = Icons.SwitchApps };

        this.Name = Strings.Command_ViewMetadata!;
        this.Title = Strings.Metadata_PageTitle!;
        this.Icon = Icons.Metadata;
        this._metadata.TemplateJson = CardTemplate;
        this._metadata.DataJson = BuildCardData(null, null);
    }

    public static MediaMetadataPage ForSession(
        IMediaService mediaService,
        MediaSessionViewModelCache viewModels,
        MediaSessionViewModel viewModel,
        MediaCommandResultFactory resultFactory,
        IIconService iconService)
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(viewModels);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(resultFactory);
        ArgumentNullException.ThrowIfNull(iconService);

        var session = viewModel.Session;
        var playPauseCommand = new OptimisticPlaybackCommand(
            mediaService,
            resultFactory,
            iconService,
            IconSurface.CommandPalette);
        playPauseCommand.UpdatePresentation(session);
        return new(
            mediaService,
            viewModels,
            session.Id,
            new PreviousTrackInvokableSpecificMediaCommand(
                mediaService,
                session,
                resultFactory),
            playPauseCommand,
            new NextTrackInvokableSpecificMediaCommand(
                mediaService,
                session,
                resultFactory),
            new ToggleShuffleSpecificMediaCommand(
                mediaService,
                session,
                resultFactory),
            new ToggleRepeatSpecificMediaCommand(
                mediaService,
                session,
                resultFactory),
            new BringAssociatedAppToFrontCommand(
                mediaService,
                viewModels,
                session.Id));
    }

    public override IContent[] GetContent() => this._content;

    protected override void OnLoaded()
    {
        lock (this._stateLock)
        {
            if (this._isLoaded)
            {
                return;
            }

            this._isLoaded = true;
            this.SubscribeToTargetChanges();
            this.SetViewModelUnderLock(this.ResolveViewModel());
        }
    }

    protected override void OnUnloaded()
    {
        lock (this._stateLock)
        {
            if (!this._isLoaded)
            {
                return;
            }

            this._isLoaded = false;
            this.UnsubscribeFromTargetChanges();
            this.SetViewModelUnderLock(null);
            this._snapshot = null;
            this.CancelArtworkLoadUnderLock();
        }
    }

    protected IMediaService MediaService => this._mediaService;

    protected MediaSessionViewModelCache ViewModels => this._viewModels;

    protected virtual MediaSessionViewModel? ResolveViewModel()
    {
        if (this._sessionId is not { } sessionId)
        {
            return null;
        }

        var session = this._mediaService.Sessions.FirstOrDefault(
            session => session.Id == sessionId);
        return session is not null
            ? this._viewModels.GetOrCreate(session)
            : null;
    }

    protected virtual void SubscribeToTargetChanges()
        => this._mediaService.SessionsChanged += this.MediaServiceOnSessionsChanged;

    protected virtual void UnsubscribeFromTargetChanges()
        => this._mediaService.SessionsChanged -= this.MediaServiceOnSessionsChanged;

    protected void RefreshTarget()
    {
        lock (this._stateLock)
        {
            if (this._isLoaded)
            {
                this.SetViewModelUnderLock(this.ResolveViewModel());
            }
        }
    }

    private void MediaServiceOnSessionsChanged(object? sender, EventArgs args)
        => this.RefreshTarget();

    private void SetViewModelUnderLock(MediaSessionViewModel? viewModel)
    {
        if (ReferenceEquals(this._viewModel, viewModel))
        {
            this.UpdateUnderLock();
            return;
        }

        if (this._viewModel is not null)
        {
            this._viewModel.Changed -= this.ViewModelOnChanged;
        }

        this._viewModel = viewModel;
        this._snapshot = null;
        this.CancelArtworkLoadUnderLock();

        if (viewModel is not null)
        {
            viewModel.Changed += this.ViewModelOnChanged;
            viewModel.RequestArtwork();
        }

        this.UpdateUnderLock();
    }

    private void ViewModelOnChanged(object? sender, EventArgs args)
    {
        lock (this._stateLock)
        {
            if (this._isLoaded && ReferenceEquals(sender, this._viewModel))
            {
                this.UpdateUnderLock();
            }
        }
    }

    private void UpdateUnderLock()
    {
        if (this._viewModel is not { IsAvailable: true } viewModel)
        {
            this._snapshot = null;
            this.Title = Strings.Metadata_PageTitle!;
            if (this._commandAvailability is not null)
            {
                this._commandAvailability = null;
                this.Commands = [];
            }

            this._metadata.DataJson = BuildCardData(null, null);
            this.CancelArtworkLoadUnderLock();
            return;
        }

        this._playPauseAction.UpdatePresentation(viewModel.Session);
        var snapshot = MediaMetadataSnapshot.FromViewModel(viewModel);
        if (this._snapshot != snapshot)
        {
            this._snapshot = snapshot;
            this.Title = MediaMetadataFormatting.ValueOrNotAvailable(snapshot.Title);
            this._metadata.DataJson = BuildCardData(snapshot, this._artworkDataUri);
        }

        var commandAvailability = MediaCommandAvailability.FromSnapshot(snapshot);
        if (this._commandAvailability != commandAvailability)
        {
            this._commandAvailability = commandAvailability;
            this.Commands = this.BuildCommands(commandAvailability);
        }

        var artworkThumbnail = viewModel.Artwork?.Stream is not null
            ? viewModel.Artwork
            : null;
        if (!ReferenceEquals(this._artworkThumbnail, artworkThumbnail))
        {
            this.CancelArtworkLoadUnderLock();
            this._artworkThumbnail = artworkThumbnail;
            this._metadata.DataJson = BuildCardData(snapshot, null);
            if (artworkThumbnail is not null)
            {
                this._artworkCancellation = new();
                _ = this.LoadArtworkAsync(viewModel, artworkThumbnail, this._artworkCancellation.Token);
            }
        }
    }

    private IContextItem[] BuildCommands(MediaCommandAvailability availability)
    {
        var commands = new List<IContextItem>(6);
        commands.Add(this._playPauseCommand);
        if (availability.CanSkipNext)
        {
            commands.Add(this._nextCommand);
        }

        if (availability.CanSkipPrevious)
        {
            commands.Add(this._previousCommand);
        }

        if (availability.CanToggleShuffle)
        {
            commands.Add(this._shuffleCommand);
        }

        if (availability.CanToggleRepeat)
        {
            commands.Add(this._repeatCommand);
        }

        commands.Add(this._switchToApplicationCommand);
        return [.. commands];
    }

    private readonly record struct MediaCommandAvailability(
        bool CanSkipPrevious,
        bool CanSkipNext,
        bool CanToggleShuffle,
        bool CanToggleRepeat)
    {
        public static MediaCommandAvailability FromSnapshot(
            MediaMetadataSnapshot snapshot) =>
            new(
                snapshot.CanSkipPrevious,
                snapshot.CanSkipNext,
                snapshot.CanToggleShuffle,
                snapshot.CanToggleRepeat);
    }

    private async Task LoadArtworkAsync(
        MediaSessionViewModel viewModel,
        ThumbnailInfo artworkThumbnail,
        CancellationToken cancellationToken)
    {
        try
        {
            var dataUri = await artworkThumbnail.GetDataUriAsync(cancellationToken);
            lock (this._stateLock)
            {
                if (!this._isLoaded ||
                    !ReferenceEquals(this._viewModel, viewModel) ||
                    !ReferenceEquals(this._artworkThumbnail, artworkThumbnail) ||
                    this._snapshot is not { } snapshot)
                {
                    return;
                }

                this._artworkDataUri = dataUri;
                this._metadata.DataJson = BuildCardData(snapshot, dataUri);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to prepare artwork for the metadata page.", ex);
        }
    }

    private void CancelArtworkLoadUnderLock()
    {
        this._artworkCancellation?.Cancel();
        this._artworkCancellation?.Dispose();
        this._artworkCancellation = null;
        this._artworkThumbnail = null;
        this._artworkDataUri = null;
    }

    private static string BuildCardData(MediaMetadataSnapshot? snapshot, string? artworkDataUri)
    {
        if (snapshot is not { } metadata)
        {
            return new JsonObject
            {
                ["hasMedia"] = false,
                ["showNoMedia"] = true,
                ["noMedia"] = Strings.Metadata_NoMedia!
            }.ToJsonString();
        }

        var hasArtwork = !string.IsNullOrWhiteSpace(artworkDataUri);
        return new JsonObject
        {
            ["hasMedia"] = true,
            ["showNoMedia"] = false,
            ["hasArtwork"] = hasArtwork,
            ["artwork"] = artworkDataUri ?? string.Empty,
            ["artworkAltText"] = Strings.Metadata_ArtworkAltText!,
            ["title"] = MediaMetadataFormatting.ValueOrNotAvailable(metadata.Title),
            ["artist"] = MediaMetadataFormatting.ValueOrNotAvailable(metadata.Artist),
            ["album"] = MediaMetadataFormatting.ValueOrNotAvailable(metadata.AlbumTitle),
            ["playbackStatus"] = metadata.IsPlaying ? Strings.Toast_Playing! : Strings.Toast_Paused!,
            ["playbackColor"] = metadata.IsPlaying ? "good" : "default",
            ["playbackType"] = MediaMetadataFormatting.FormatPlaybackType(metadata.PlaybackType),
            ["player"] = MediaMetadataFormatting.ValueOrNotAvailable(metadata.Player),
            ["metadataSection"] = Strings.Metadata_Section!,
            ["albumLabel"] = Strings.Details_Album!,
            ["albumArtistLabel"] = Strings.Details_AlbumArtist!,
            ["albumArtist"] = MediaMetadataFormatting.ValueOrNotAvailable(metadata.AlbumArtist),
            ["subtitleLabel"] = Strings.Details_Subtitle!,
            ["subtitle"] = MediaMetadataFormatting.ValueOrNotAvailable(metadata.Subtitle),
            ["genresLabel"] = Strings.Details_Genres!,
            ["genres"] = MediaMetadataFormatting.ValueOrNotAvailable(metadata.Genres),
            ["trackNumberLabel"] = Strings.Details_TrackNumber!,
            ["trackNumber"] = MediaMetadataFormatting.FormatTrackNumber(metadata.TrackNumber, metadata.AlbumTrackCount),
            ["lengthLabel"] = Strings.Details_Length!,
            ["length"] = MediaMetadataFormatting.FormatTrackLength(metadata.TrackLength),
            ["technicalSection"] = Strings.Metadata_TechnicalSection!,
            ["applicationIdLabel"] = Strings.Details_ApplicationId!,
            ["applicationId"] = MediaMetadataFormatting.ValueOrNotAvailable(metadata.ApplicationId)
        }.ToJsonString();
    }

    private const string CardTemplate = """
{
    "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
    "type": "AdaptiveCard",
    "version": "1.5",
    "body": [
        {
            "type": "TextBlock",
            "text": "${noMedia}",
            "size": "large",
            "weight": "bolder",
            "wrap": true,
            "isVisible": "${showNoMedia}"
        },
        {
            "type": "ColumnSet",
            "isVisible": "${hasMedia}",
            "columns": [
                {
                    "type": "Column",
                    "width": 1,
                    "isVisible": "${hasArtwork}",
                    "items": [
                        {
                            "type": "Image",
                            "url": "${artwork}",
                            "altText": "${artworkAltText}",
                            "size": "stretch",
                            "horizontalAlignment": "center"
                        }
                    ]
                },
                {
                    "type": "Column",
                    "width": 2,
                    "spacing": "large",
                    "items": [
                        {
                            "type": "TextBlock",
                            "text": "${title}",
                            "size": "extraLarge",
                            "weight": "bolder",
                            "style": "heading",
                            "wrap": true
                        },
                        {
                            "type": "TextBlock",
                            "text": "${artist}",
                            "size": "large",
                            "weight": "bolder",
                            "wrap": true,
                            "spacing": "small"
                        },
                        {
                            "type": "TextBlock",
                            "text": "${album}",
                            "isSubtle": true,
                            "wrap": true,
                            "spacing": "small"
                        },
                        {
                            "type": "TextBlock",
                            "text": "${playbackStatus} · ${playbackType} · ${player}",
                            "color": "${playbackColor}",
                            "weight": "bolder",
                            "wrap": true,
                            "spacing": "medium"
                        },
                        {
                            "type": "Container",
                            "style": "emphasis",
                            "spacing": "large",
                            "items": [
                                {
                                    "type": "TextBlock",
                                    "text": "${metadataSection}",
                                    "size": "medium",
                                    "weight": "bolder",
                                    "style": "heading",
                                    "wrap": true
                                },
                                {
                                    "type": "FactSet",
                                    "facts": [
                                        { "title": "${albumLabel}", "value": "${album}" },
                                        { "title": "${albumArtistLabel}", "value": "${albumArtist}" },
                                        { "title": "${subtitleLabel}", "value": "${subtitle}" },
                                        { "title": "${genresLabel}", "value": "${genres}" },
                                        { "title": "${trackNumberLabel}", "value": "${trackNumber}" },
                                        { "title": "${lengthLabel}", "value": "${length}" }
                                    ]
                                }
                            ]
                        },
                        {
                            "type": "TextBlock",
                            "text": "${technicalSection}",
                            "size": "medium",
                            "weight": "bolder",
                            "style": "heading",
                            "wrap": true,
                            "separator": true,
                            "spacing": "large"
                        },
                        {
                            "type": "FactSet",
                            "facts": [
                                { "title": "${applicationIdLabel}", "value": "${applicationId}" }
                            ]
                        }
                    ]
                }
            ]
        }
    ]
}
""";
}
#endif
