// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

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