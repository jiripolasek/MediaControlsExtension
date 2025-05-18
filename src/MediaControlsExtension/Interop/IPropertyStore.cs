using System.Runtime.InteropServices;

namespace JPSoftworks.MediaControlsExtension.Interop;

[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPropertyStore
{
    [PreserveSig]
    int GetCount([Out] out uint cProps);
    [PreserveSig]
    int GetAt([In] uint iProp, out PROPERTYKEY pkey);
    PropVariant GetValue([In] ref PROPERTYKEY key);
    [PreserveSig]
    int SetValue([In] ref PROPERTYKEY key, [In] ref PropVariant pv);
    [PreserveSig]
    int Commit();
}