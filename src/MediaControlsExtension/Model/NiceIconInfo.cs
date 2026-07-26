// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Model;

internal sealed class NiceIconInfo : IEquatable<NiceIconInfo>
{
    private readonly IconDataSource _source;
    private readonly string? _stringIcon;
    private readonly string? _thumbnailHash;

    public IconInfo? IconInfo { get; }

    public NiceIconInfo(string icon)
    {
        this._source = IconDataSource.String;
        this._stringIcon = icon;
        this._thumbnailHash = null;
        this.IconInfo = new(icon);
    }

    public NiceIconInfo(IconInfo iconInfo)
    {
        ArgumentNullException.ThrowIfNull(iconInfo);
        if (iconInfo.Light.Icon == null || iconInfo.Dark.Icon == null)
        {
            throw new ArgumentException("Both light and dark icons must be provided in IconInfo.", nameof(iconInfo));
        }

        this.IconInfo = iconInfo;
        this._source = IconDataSource.DoubleString;
        this._stringIcon = null;
        this._thumbnailHash = null;
    }

    public NiceIconInfo(ThumbnailInfo thumbnailInfo)
    {
        ArgumentNullException.ThrowIfNull(thumbnailInfo);

        this._thumbnailHash = thumbnailInfo.Hash;
        this._source = IconDataSource.Binary;
        this._stringIcon = null;
        this.IconInfo = thumbnailInfo.GetIcon();
    }

    public bool Equals(NiceIconInfo? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (this._source != other._source)
        {
            return false;
        }

        return this._source switch
        {
            IconDataSource.String => StringComparer.Ordinal.Equals(this._stringIcon, other._stringIcon),
            IconDataSource.Binary => StringComparer.Ordinal.Equals(this._thumbnailHash, other._thumbnailHash),
            IconDataSource.DoubleString => this.IconInfo!.Light.Icon == other.IconInfo!.Light.Icon && this.IconInfo.Dark.Icon == other.IconInfo.Dark.Icon,
            _ => false
        };
    }

    public override bool Equals(object? obj)
    {
        return obj is NiceIconInfo other && this.Equals(other);
    }

    public override int GetHashCode()
    {
        return this._source switch
        {
            IconDataSource.String => HashCode.Combine(this._source, this._stringIcon),
            IconDataSource.Binary => HashCode.Combine(this._source, this._thumbnailHash),
            IconDataSource.DoubleString => HashCode.Combine(this._source, this.IconInfo!.Light.Icon, this.IconInfo.Dark.Icon),
            _ => this._source.GetHashCode()
        };
    }

    public static bool operator ==(NiceIconInfo? left, NiceIconInfo? right)
    {
        return Equals(left, right);
    }

    public static bool operator !=(NiceIconInfo? left, NiceIconInfo? right)
    {
        return !Equals(left, right);
    }
}
