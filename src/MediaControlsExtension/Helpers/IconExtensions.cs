// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal static class IconExtensions
{
    public static bool UpdateIcon(this CommandItem item, IIconInfo? icon)
    {
        if(ReferenceEquals(icon, item.Icon))
        {
            return false;
        }

        if (item.Icon == icon || (item.Icon?.Dark?.Icon == icon?.Dark?.Icon && item.Icon?.Light?.Icon == icon?.Light?.Icon))
        {
            return false;
        }

        item.Icon = icon;
        return true;
    }

    public static bool UpdateIcon(this Command command, IconInfo icon)
    {
        if(ReferenceEquals(icon, command.Icon))
        {
            return false;
        }

        if (command.Icon == icon || (command.Icon?.Dark?.Icon == icon.Dark?.Icon && command.Icon?.Light?.Icon == icon.Light.Icon))
        {
            return false;
        }

        command.Icon = icon;
        return true;
    }
}