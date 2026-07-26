// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Pages;

internal sealed partial class DetailsForwardingListItem : ListItemBase, IDisposable
{
    private NowPlayingListItem? _detailsSource;
    private int _disposed;

    public override IDetails? Details
    {
        get => Volatile.Read(ref this._detailsSource)?.Details;
        set
        {
        }
    }

    public DetailsForwardingListItem(
        ICommand command,
        NowPlayingListItem detailsSource) : base(command)
    {
        ArgumentNullException.ThrowIfNull(detailsSource);

        this._detailsSource = detailsSource;
        detailsSource.DetailsChanged += this.DetailsSourceOnDetailsChanged;
    }

    private void DetailsSourceOnDetailsChanged(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref this._disposed) == 0 &&
            ReferenceEquals(sender, Volatile.Read(ref this._detailsSource)))
        {
            this.OnPropertyChanged(nameof(this.Details));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref this._disposed, 1) != 0)
        {
            return;
        }

        var detailsSource = Interlocked.Exchange(ref this._detailsSource, null);
        if (detailsSource is not null)
        {
            detailsSource.DetailsChanged -= this.DetailsSourceOnDetailsChanged;
        }
    }
}
