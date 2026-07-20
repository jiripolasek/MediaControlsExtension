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
internal sealed partial class MediaMetadataPage : VisibilityAwareContentPage
{
    private enum TargetMode
    {
        FixedSource,
        CurrentSource
    }

    private readonly Lock _stateLock = new();
    private readonly MediaService _mediaService;
    private readonly MediaSource? _fixedSource;
    private readonly TargetMode _targetMode;
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

    private MediaSource? _mediaSource;
    private ThumbnailInfo? _artworkThumbnail;
    private CancellationTokenSource? _artworkCancellation;
    private string? _artworkDataUri;
    private MediaMetadataSnapshot? _snapshot;
    private bool _isLoaded;

    private MediaMetadataPage(
        MediaService mediaService,
        MediaSource? fixedSource,
        TargetMode targetMode,
        ICommand previousCommand,
        ICommand playPauseCommand,
        ICommand nextCommand,
        ICommand shuffleCommand,
        ICommand repeatCommand,
        ICommand switchToApplicationCommand)
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(previousCommand);
        ArgumentNullException.ThrowIfNull(playPauseCommand);
        ArgumentNullException.ThrowIfNull(nextCommand);
        ArgumentNullException.ThrowIfNull(shuffleCommand);
        ArgumentNullException.ThrowIfNull(repeatCommand);
        ArgumentNullException.ThrowIfNull(switchToApplicationCommand);

        this._mediaService = mediaService;
        this._fixedSource = fixedSource;
        this._targetMode = targetMode;
        this._content = [this._metadata];
        this._previousCommand = new CommandContextItem(previousCommand) { RequestedShortcut = Chords.PreviousTrack, Icon = Icons.PreviousTrackOutline };
        this._playPauseCommand = new CommandContextItem(playPauseCommand) { RequestedShortcut = Chords.PlayPause };
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

    public static MediaMetadataPage ForSource(
        MediaService mediaService,
        MediaSource mediaSource,
        ICommand previousCommand,
        ICommand playPauseCommand,
        ICommand nextCommand,
        ICommand shuffleCommand,
        ICommand repeatCommand,
        ICommand switchToApplicationCommand)
    {
        ArgumentNullException.ThrowIfNull(mediaSource);

        return new(
            mediaService,
            mediaSource,
            TargetMode.FixedSource,
            previousCommand,
            playPauseCommand,
            nextCommand,
            shuffleCommand,
            repeatCommand,
            switchToApplicationCommand);
    }

    public static MediaMetadataPage ForNowPlaying(
        MediaService mediaService,
        ICommand previousCommand,
        ICommand playPauseCommand,
        ICommand nextCommand,
        ICommand shuffleCommand,
        ICommand repeatCommand,
        ICommand switchToApplicationCommand)
    {
        return new(
            mediaService,
            null,
            TargetMode.CurrentSource,
            previousCommand,
            playPauseCommand,
            nextCommand,
            shuffleCommand,
            repeatCommand,
            switchToApplicationCommand);
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
            if (this._targetMode == TargetMode.CurrentSource)
            {
                this._mediaService.CurrentMediaSourceChanged += this.CurrentMediaSourceChanged;
            }
            else
            {
                this._mediaService.MediaSourcesChanged += this.MediaSourcesChanged;
            }

            this.SetMediaSourceUnderLock(this.ResolveMediaSource());
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
            this._mediaService.CurrentMediaSourceChanged -= this.CurrentMediaSourceChanged;
            this._mediaService.MediaSourcesChanged -= this.MediaSourcesChanged;
            this.SetMediaSourceUnderLock(null);
            this._snapshot = null;
            this.CancelArtworkLoadUnderLock();
        }
    }

    private MediaSource? ResolveMediaSource()
    {
        if (this._targetMode == TargetMode.CurrentSource)
        {
            return this._mediaService.CurrentSource;
        }

        return this._fixedSource is not null && this._mediaService.Sources.Contains(this._fixedSource)
            ? this._fixedSource
            : null;
    }

    private void CurrentMediaSourceChanged(object? sender, MediaSource? mediaSource)
    {
        lock (this._stateLock)
        {
            if (this._isLoaded)
            {
                this.SetMediaSourceUnderLock(mediaSource);
            }
        }
    }

    private void MediaSourcesChanged(object? sender, EventArgs args)
    {
        lock (this._stateLock)
        {
            if (this._isLoaded)
            {
                this.SetMediaSourceUnderLock(this.ResolveMediaSource());
            }
        }
    }

    private void SetMediaSourceUnderLock(MediaSource? mediaSource)
    {
        if (ReferenceEquals(this._mediaSource, mediaSource))
        {
            this.UpdateUnderLock();
            return;
        }

        if (this._mediaSource is not null)
        {
            this._mediaSource.PropChanged -= this.MediaSourceOnPropChanged;
        }

        this._mediaSource = mediaSource;
        this._snapshot = null;
        this.CancelArtworkLoadUnderLock();

        if (mediaSource is not null)
        {
            mediaSource.PropChanged += this.MediaSourceOnPropChanged;
            mediaSource.RequestHeroThumbnail();
        }

        this.UpdateUnderLock();
    }

    private void MediaSourceOnPropChanged(object sender, IPropChangedEventArgs args)
    {
        lock (this._stateLock)
        {
            if (this._isLoaded && ReferenceEquals(sender, this._mediaSource))
            {
                this.UpdateUnderLock();
            }
        }
    }

    private void UpdateUnderLock()
    {
        if (this._mediaSource is not { HasProperties: true } mediaSource)
        {
            this.Title = Strings.Metadata_PageTitle!;
            this.Commands = [];
            this._metadata.DataJson = BuildCardData(null, null);
            this.CancelArtworkLoadUnderLock();
            return;
        }

        var snapshot = MediaMetadataSnapshot.FromMediaSource(mediaSource);
        if (this._snapshot != snapshot)
        {
            this._snapshot = snapshot;
            this.Title = MediaMetadataFormatting.ValueOrNotAvailable(snapshot.Title);
            this._metadata.DataJson = BuildCardData(snapshot, this._artworkDataUri);
            this.Commands = this.BuildCommands(snapshot);
        }

        var artworkThumbnail = mediaSource.HeroThumbnailInfo?.Stream is not null
            ? mediaSource.HeroThumbnailInfo
            : null;
        if (!ReferenceEquals(this._artworkThumbnail, artworkThumbnail))
        {
            this.CancelArtworkLoadUnderLock();
            this._artworkThumbnail = artworkThumbnail;
            this._metadata.DataJson = BuildCardData(snapshot, null);
            if (artworkThumbnail is not null)
            {
                this._artworkCancellation = new();
                _ = this.LoadArtworkAsync(mediaSource, artworkThumbnail, this._artworkCancellation.Token);
            }
        }
    }

    private IContextItem[] BuildCommands(MediaMetadataSnapshot snapshot)
    {
        var commands = new List<IContextItem>(6);
        commands.Add(this._playPauseCommand);
        if (snapshot.CanSkipNext)
        {
            commands.Add(this._nextCommand);
        }

        if (snapshot.CanSkipPrevious)
        {
            commands.Add(this._previousCommand);
        }

        if (snapshot.CanToggleShuffle)
        {
            commands.Add(this._shuffleCommand);
        }

        if (snapshot.CanToggleRepeat)
        {
            commands.Add(this._repeatCommand);
        }

        commands.Add(this._switchToApplicationCommand);
        return [.. commands];
    }

    private async Task LoadArtworkAsync(
        MediaSource mediaSource,
        ThumbnailInfo artworkThumbnail,
        CancellationToken cancellationToken)
    {
        try
        {
            var dataUri = await artworkThumbnail.GetDataUriAsync(cancellationToken);
            lock (this._stateLock)
            {
                if (!this._isLoaded ||
                    !ReferenceEquals(this._mediaSource, mediaSource) ||
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
