// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Pages;

internal sealed class MediaDetails
{
    private readonly MediaDetailsState _state;

    public IDetails Details { get; }

    public MediaDetails(
        ICommand previousCommand,
        ICommand playPauseCommand,
        ICommand nextCommand,
        ICommand switchToApplicationCommand,
        MediaSessionViewModel viewModel,
        ICommand? viewMetadataCommand = null)
    {
        ArgumentNullException.ThrowIfNull(previousCommand);
        ArgumentNullException.ThrowIfNull(playPauseCommand);
        ArgumentNullException.ThrowIfNull(nextCommand);
        ArgumentNullException.ThrowIfNull(switchToApplicationCommand);
        ArgumentNullException.ThrowIfNull(viewModel);

        var artwork = viewModel.Artwork;
        this._state = MediaDetailsState.FromViewModel(viewModel, artwork?.Hash);
        var commands = new DetailsCommands
        {
            Commands = BuildCommands(
                this._state,
                previousCommand,
                playPauseCommand,
                nextCommand,
                switchToApplicationCommand,
                viewMetadataCommand),
        };

        this.Details = new Details
        {
            HeroImage = artwork?.GetIcon()
                ?? Icons.MediaHeroPlaceholder,
            Title = MediaMetadataFormatting.ValueOrNotAvailable(this._state.Title),
            Metadata =
            [
                Detail(Strings.Details_Title!, this._state.Title),
                Detail(Strings.Details_Album!, this._state.AlbumTitle),
                Detail(Strings.Details_Artist!, this._state.Artist),
                Detail(
                    Strings.Details_Length!,
                    MediaMetadataFormatting.FormatTrackLength(this._state.TrackLength)),
                Detail(Strings.Details_Player!, this._state.Player),
                new DetailsElement
                {
                    Key = Strings.Details_Commands!,
                    Data = commands,
                },
            ],
        };
    }

    public bool Represents(MediaSessionViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return this._state == MediaDetailsState.FromViewModel(viewModel);
    }

    private static DetailsElement Detail(string key, string? value)
    {
        return new DetailsElement
        {
            Key = key,
            Data = new DetailsLink
            {
                Text = MediaMetadataFormatting.ValueOrNotAvailable(value),
            },
        };
    }

    private static ICommand[] BuildCommands(
        MediaDetailsState state,
        ICommand previousCommand,
        ICommand playPauseCommand,
        ICommand nextCommand,
        ICommand switchToApplicationCommand,
        ICommand? viewMetadataCommand)
    {
        var commands = new List<ICommand>(5)
        {
            playPauseCommand,
        };

        if (state.CanSkipNext)
        {
            commands.Add(nextCommand);
        }

        if (state.CanSkipPrevious)
        {
            commands.Add(previousCommand);
        }

        commands.Add(switchToApplicationCommand);
        if (viewMetadataCommand is not null)
        {
            commands.Add(viewMetadataCommand);
        }

        return [.. commands];
    }

    private readonly record struct MediaDetailsState(
        MediaSessionId SessionId,
        string Title,
        string AlbumTitle,
        string Artist,
        TimeSpan? TrackLength,
        string Player,
        bool CanSkipPrevious,
        bool CanSkipNext,
        string? ArtworkHash)
    {
        public static MediaDetailsState FromViewModel(MediaSessionViewModel viewModel)
            => FromViewModel(viewModel, viewModel.Artwork?.Hash);

        public static MediaDetailsState FromViewModel(
            MediaSessionViewModel viewModel,
            string? artworkHash)
        {
            var properties = viewModel.MediaProperties;
            var playback = viewModel.PlaybackInfo;
            return new(
                viewModel.Session.Id,
                properties.Title,
                properties.AlbumTitle,
                properties.Artist,
                viewModel.TimelineProperties.Duration,
                viewModel.ApplicationName,
                playback.Capabilities.HasFlag(MediaCapabilities.SkipPrevious),
                playback.Capabilities.HasFlag(MediaCapabilities.SkipNext),
                artworkHash);
        }
    }
}
