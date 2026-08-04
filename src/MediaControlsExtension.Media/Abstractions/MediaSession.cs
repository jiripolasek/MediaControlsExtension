// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Media;

public sealed class MediaSessionChangedEventArgs(
    long revision,
    MediaSessionChanges changes) : EventArgs
{
    public long Revision { get; } = revision;

    public MediaSessionChanges Changes { get; } = changes;
}

public sealed class MediaSession
{
    private MediaSessionState _state;
    private long _bindingGeneration;

    internal MediaSession(MediaSessionSnapshot snapshot)
    {
        this.Id = snapshot.Id;
        this._bindingGeneration = snapshot.BindingGeneration;
        this._state = new(
            1,
            snapshot.IsAvailable,
            snapshot.MediaProperties,
            snapshot.TimelineProperties,
            snapshot.PlaybackInfo);
    }

    public event EventHandler<MediaSessionChangedEventArgs>? Changed;

    public MediaSessionId Id { get; }

    private MediaSessionState State => Volatile.Read(ref this._state);

    public long Revision => this.State.Revision;

    public bool IsAvailable => this.State.IsAvailable;

    public MediaPropertiesSnapshot MediaProperties => this.State.MediaProperties;

    public MediaTimelinePropertiesSnapshot TimelineProperties => this.State.TimelineProperties;

    public MediaPlaybackInfoSnapshot PlaybackInfo => this.State.PlaybackInfo;

    internal MediaSessionChanges Apply(MediaSessionSnapshot snapshot)
    {
        var previous = this.State;
        var changes = MediaSessionChanges.None;

        var mediaProperties = AreEquivalent(previous.MediaProperties, snapshot.MediaProperties)
            ? previous.MediaProperties
            : snapshot.MediaProperties;
        if (!ReferenceEquals(mediaProperties, previous.MediaProperties))
        {
            changes |= MediaSessionChanges.MediaProperties;
        }

        var timelineProperties = snapshot.TimelineProperties == previous.TimelineProperties
            ? previous.TimelineProperties
            : snapshot.TimelineProperties;
        if (!ReferenceEquals(timelineProperties, previous.TimelineProperties))
        {
            changes |= MediaSessionChanges.TimelineProperties;
        }

        var playbackInfo = snapshot.PlaybackInfo == previous.PlaybackInfo
            ? previous.PlaybackInfo
            : snapshot.PlaybackInfo;
        if (!ReferenceEquals(playbackInfo, previous.PlaybackInfo))
        {
            changes |= MediaSessionChanges.PlaybackInfo;
        }

        if (snapshot.IsAvailable != previous.IsAvailable)
        {
            changes |= MediaSessionChanges.Availability;
        }

        if (snapshot.BindingGeneration != this._bindingGeneration)
        {
            this._bindingGeneration = snapshot.BindingGeneration;
            changes |= MediaSessionChanges.Rebound;
        }

        if (changes == MediaSessionChanges.None)
        {
            return changes;
        }

        Volatile.Write(
            ref this._state,
            new(
                previous.Revision + 1,
                snapshot.IsAvailable,
                mediaProperties,
                timelineProperties,
                playbackInfo));
        return changes;
    }

    internal MediaSessionChanges MarkUnavailable()
    {
        var previous = this.State;
        if (!previous.IsAvailable)
        {
            return MediaSessionChanges.None;
        }

        Volatile.Write(
            ref this._state,
            previous with
            {
                Revision = previous.Revision + 1,
                IsAvailable = false,
            });
        return MediaSessionChanges.Availability;
    }

    internal void RaiseChanged(MediaSessionChanges changes, Action<Exception> reportException)
    {
        var handler = this.Changed;
        if (handler is null || changes == MediaSessionChanges.None)
        {
            return;
        }

        var args = new MediaSessionChangedEventArgs(this.Revision, changes);
        foreach (EventHandler<MediaSessionChangedEventArgs> subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(this, args);
            }
            catch (Exception ex)
            {
                reportException(ex);
            }
        }
    }

    private static bool AreEquivalent(
        MediaPropertiesSnapshot left,
        MediaPropertiesSnapshot right)
    {
        return left.Application == right.Application
            && string.Equals(left.Title, right.Title, StringComparison.Ordinal)
            && string.Equals(left.Artist, right.Artist, StringComparison.Ordinal)
            && string.Equals(left.AlbumTitle, right.AlbumTitle, StringComparison.Ordinal)
            && string.Equals(left.AlbumArtist, right.AlbumArtist, StringComparison.Ordinal)
            && string.Equals(left.Subtitle, right.Subtitle, StringComparison.Ordinal)
            && left.Genres.AsSpan().SequenceEqual(right.Genres.AsSpan())
            && left.TrackNumber == right.TrackNumber
            && left.AlbumTrackCount == right.AlbumTrackCount
            && left.ContentType == right.ContentType
            && left.Artwork == right.Artwork;
    }
}