// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace JPSoftworks.MediaControlsExtension.Interop;

[GeneratedComInterface]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
internal partial interface IShellItem
{
    [PreserveSig]
    int BindToHandler(nint bindContext, in Guid handlerId, in Guid interfaceId, out nint result);

    [PreserveSig]
    int GetParent(out nint parent);

    [PreserveSig]
    int GetDisplayName(uint displayNameType, out nint displayName);

    [PreserveSig]
    int GetAttributes(uint attributeMask, out uint attributes);

    [PreserveSig]
    int Compare(nint shellItem, uint hint, out int order);
}

[GeneratedComInterface]
[Guid("7E9FB0D3-919F-4307-AB2E-9B1860310C93")]
internal partial interface IShellItem2 : IShellItem
{
    [PreserveSig]
    int GetPropertyStore(uint flags, in Guid interfaceId, out nint propertyStore);

    [PreserveSig]
    int GetPropertyStoreWithCreateObject(uint flags, nint createObject, in Guid interfaceId, out nint propertyStore);

    [PreserveSig]
    int GetPropertyStoreForKeys(nint keys, uint keyCount, uint flags, in Guid interfaceId, out nint propertyStore);

    [PreserveSig]
    int GetPropertyDescriptionList(in PROPERTYKEY keyType, in Guid interfaceId, out nint propertyDescriptionList);

    [PreserveSig]
    int Update(nint bindContext);

    [PreserveSig]
    int GetProperty(in PROPERTYKEY key, nint value);

    [PreserveSig]
    int GetCLSID(in PROPERTYKEY key, out Guid classId);

    [PreserveSig]
    int GetFileTime(in PROPERTYKEY key, out long fileTime);

    [PreserveSig]
    int GetInt32(in PROPERTYKEY key, out int value);

    [PreserveSig]
    int GetString(in PROPERTYKEY key, out nint value);
}
