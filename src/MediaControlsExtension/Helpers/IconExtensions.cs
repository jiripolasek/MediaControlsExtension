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
        if (HasSameIcon(item.Icon, icon))
        {
            return false;
        }

        item.Icon = icon;
        return true;
    }

    public static bool UpdateIcon(this Command command, IconInfo? icon)
    {
        if (HasSameIcon(command.Icon, icon))
        {
            return false;
        }

        command.Icon = icon!;
        return true;
    }

    private static bool HasSameIcon(IIconInfo? left, IIconInfo? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return HasSameIconData(left.Dark, right.Dark) && HasSameIconData(left.Light, right.Light);
    }

    private static bool HasSameIconData(IIconData? left, IIconData? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return string.Equals(left.Icon, right.Icon, StringComparison.Ordinal) && ReferenceEquals(left.Data, right.Data);
    }
}