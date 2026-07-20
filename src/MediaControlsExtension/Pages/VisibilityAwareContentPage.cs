// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using Windows.Foundation;

namespace JPSoftworks.MediaControlsExtension.Pages;

#if FF_ENABLE_FULL_METADATA_PAGE
/// <summary>
/// A content page that starts work only while CmdPal is displaying it.
/// CmdPal attaches an <see cref="ItemsChanged"/> handler when a page enters
/// the UI and removes it when the page leaves the navigation stack.
/// </summary>
internal abstract partial class VisibilityAwareContentPage : Page, IContentPage
{
    private readonly Lock _loadLock = new();
    private event TypedEventHandler<object, IItemsChangedEventArgs>? InternalItemsChanged;
    private int _loadCount;

    public event TypedEventHandler<object, IItemsChangedEventArgs> ItemsChanged
    {
        add
        {
            lock (this._loadLock)
            {
                this.InternalItemsChanged += value;
                if (this._loadCount == 0)
                {
                    this.OnLoaded();
                }

                this._loadCount++;
            }
        }
        remove
        {
            lock (this._loadLock)
            {
                this.InternalItemsChanged -= value;
                this._loadCount = Math.Max(0, this._loadCount - 1);
                if (this._loadCount == 0)
                {
                    this.OnUnloaded();
                }
            }
        }
    }

    public virtual IDetails? Details { get; set => this.SetProperty(ref field, value); }

    public virtual IContextItem[] Commands { get; set => this.SetProperty(ref field, value); } = [];

    public abstract IContent[] GetContent();

    protected abstract void OnLoaded();

    protected abstract void OnUnloaded();

    protected void RaiseItemsChanged(int totalItems = -1)
    {
        try
        {
            this.InternalItemsChanged?.Invoke(this, new ItemsChangedEventArgs(totalItems));
        }
        catch
        {
        }
    }
}
#endif
