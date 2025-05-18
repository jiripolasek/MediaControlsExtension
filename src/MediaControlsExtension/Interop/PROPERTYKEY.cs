// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace JPSoftworks.MediaControlsExtension.Interop;

[StructLayout(LayoutKind.Sequential)]
[SuppressMessage("ReSharper", "InconsistentNaming")]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Native name")]
public struct PROPERTYKEY : IEquatable<PROPERTYKEY>
{
    public Guid fmtid;
    public nuint pid;

    private PROPERTYKEY(Guid fmtid, nuint pid)
    {
        this.fmtid = fmtid;
        this.pid = pid;
    }

    public static PROPERTYKEY FromString(string fmtid, nuint pid)
    {
        return new PROPERTYKEY(Guid.Parse(fmtid), pid);
    }

    public readonly bool Equals(PROPERTYKEY other)
    {
        return this.fmtid.Equals(other.fmtid) && this.pid == other.pid;
    }

    public override readonly bool Equals(object? obj)
    {
        return obj is PROPERTYKEY other && this.Equals(other);
    }

    public static bool operator ==(PROPERTYKEY left, PROPERTYKEY right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(PROPERTYKEY left, PROPERTYKEY right)
    {
        return !left.Equals(right);
    }

    public override readonly int GetHashCode()
    {
        return HashCode.Combine(this.fmtid, this.pid);
    }
}