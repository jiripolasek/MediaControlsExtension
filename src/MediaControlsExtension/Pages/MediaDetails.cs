// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Pages;

internal sealed class MediaDetails
{
    private readonly Details _details = new();
    private readonly DetailsCommands _commands;
    private readonly ICommand _previousCommand;
    private readonly ICommand _playPauseCommand;
    private readonly ICommand _nextCommand;
    private readonly ICommand _switchToApplicationCommand;
    private readonly ICommand? _viewMetadataCommand;
    private NiceIconInfo? _heroImage;
    private ThumbnailInfo? _heroThumbnailInfo;
    private MediaMetadataSnapshot? _metadataState;

    public IDetails Details => this._details;

    public MediaDetails(
        ICommand previousCommand,
        ICommand playPauseCommand,
        ICommand nextCommand,
        ICommand switchToApplicationCommand,
        ICommand? viewMetadataCommand = null)
    {
        ArgumentNullException.ThrowIfNull(previousCommand);
        ArgumentNullException.ThrowIfNull(playPauseCommand);
        ArgumentNullException.ThrowIfNull(nextCommand);
        ArgumentNullException.ThrowIfNull(switchToApplicationCommand);

        this._previousCommand = previousCommand;
        this._playPauseCommand = playPauseCommand;
        this._nextCommand = nextCommand;
        this._switchToApplicationCommand = switchToApplicationCommand;
        this._viewMetadataCommand = viewMetadataCommand;
        this._commands = new();
    }

    public void Update(MediaSource mediaSource)
    {
        ArgumentNullException.ThrowIfNull(mediaSource);

        this.UpdateHeroImage(mediaSource.HeroThumbnailInfo);

        var metadataState = MediaMetadataSnapshot.FromMediaSource(mediaSource);
        if (this._metadataState == metadataState)
        {
            return;
        }

        this._metadataState = metadataState;
        this._details.Title = MediaMetadataFormatting.ValueOrNotAvailable(metadataState.Title);
        this._commands.Commands = this.BuildCommands(metadataState);
        this._details.Metadata =
        [
            Detail(Strings.Details_Title!, metadataState.Title),
            Detail(Strings.Details_Album!, metadataState.AlbumTitle),
            Detail(Strings.Details_Artist!, metadataState.Artist),
            Detail(Strings.Details_Length!, MediaMetadataFormatting.FormatTrackLength(metadataState.TrackLength)),
            Detail(Strings.Details_Player!, metadataState.Player),
            new DetailsElement
            {
                Key = Strings.Details_Commands!,
                Data = this._commands
            }
        ];
    }

    public void Clear()
    {
        if (this._heroImage is not null)
        {
            this._heroImage = null;
            this._heroThumbnailInfo = null;
            this._details.HeroImage = new IconInfo(string.Empty);
        }

        if (this._metadataState is not null)
        {
            this._metadataState = null;
            this._details.Title = string.Empty;
            this._details.Metadata = [];
        }
    }

    private void UpdateHeroImage(ThumbnailInfo? thumbnailInfo)
    {
        if (ReferenceEquals(this._heroThumbnailInfo, thumbnailInfo))
        {
            return;
        }

        this._heroThumbnailInfo = thumbnailInfo;
        var heroImage = thumbnailInfo?.Stream is not null
            ? new NiceIconInfo(thumbnailInfo)
            : null;

        this._heroImage = heroImage;
        this._details.HeroImage = heroImage?.IconInfo ?? new IconInfo(string.Empty);
    }

    private static DetailsElement Detail(string key, string? value)
    {
        return new DetailsElement
        {
            Key = key,
            Data = new DetailsLink { Text = MediaMetadataFormatting.ValueOrNotAvailable(value) }
        };
    }

    private ICommand[] BuildCommands(MediaMetadataSnapshot metadataState)
    {
        var commands = new List<ICommand>(5);
        commands.Add(this._playPauseCommand);
        if (metadataState.CanSkipNext)
        {
            commands.Add(this._nextCommand);
        }

        if (metadataState.CanSkipPrevious)
        {
            commands.Add(this._previousCommand);
        }

        commands.Add(this._switchToApplicationCommand);
        if (this._viewMetadataCommand is not null)
        {
            commands.Add(this._viewMetadataCommand);
        }

        return [.. commands];
    }
}
