using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace JPSoftworks.MediaControlsExtension.Interop;

[SuppressMessage("ReSharper", "InconsistentNaming")]
internal static unsafe partial class NativeMethods
{
    internal const int WAIT_TIMEOUT = 0x00000102;

    private static readonly Guid ShellItem2InterfaceId = new("7E9FB0D3-919F-4307-AB2E-9B1860310C93");

    internal static (string DisplayName, string Path) GetAppsFolderProperties(string appId)
    {
        using var comApartment = ComApartment.Enter();
        nint shellItem = 0;
        try
        {
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(
                $"shell:AppsFolder\\{appId}",
                0,
                ShellItem2InterfaceId,
                out shellItem));

            var shellItemObject = ComInterfaceMarshaller<IShellItem2>.ConvertToManaged((void*)shellItem)
                ?? throw new InvalidCastException("The AppsFolder item does not expose IShellItem2.");

            return (
                GetStringProperty(shellItemObject, PropertyKeys.PKEY_ItemNameDisplay),
                GetStringProperty(shellItemObject, PropertyKeys.PKEY_Link_TargetParsingPath));
        }
        finally
        {
            ComInterfaceMarshaller<IShellItem2>.Free((void*)shellItem);
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(
        string path,
        nint bindContext,
        in Guid interfaceId,
        out nint shellItem);

    [LibraryImport("ole32.dll")]
    private static partial void CoTaskMemFree(nint value);

    private static string GetStringProperty(IShellItem2 shellItem, in PROPERTYKEY key)
    {
        nint value = 0;
        try
        {
            Marshal.ThrowExceptionForHR(shellItem.GetString(key, out value));
            return Marshal.PtrToStringUni(value) ?? string.Empty;
        }
        finally
        {
            CoTaskMemFree(value);
        }
    }

}
