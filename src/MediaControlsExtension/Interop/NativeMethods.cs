using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace JPSoftworks.MediaControlsExtension.Interop;

[SuppressMessage("ReSharper", "InconsistentNaming")]
internal static unsafe partial class NativeMethods
{
    internal static readonly Guid FOLDERID_AppsFolder = Guid.Parse("{1e87508d-89c2-42f0-8a7e-645a0f50ca58}");

    internal const int KF_FLAG_DONT_VERIFY = 0x00004000;

    internal const int WAIT_TIMEOUT = 0x00000102;

    private const int GetStringSlot = 18;
    private const int ReleaseSlot = 2;
    private static readonly Guid ShellItem2InterfaceId = new("7E9FB0D3-919F-4307-AB2E-9B1860310C93");

    internal static (string DisplayName, string Path) GetAppsFolderProperties(string appId)
    {
        using var comApartment = ComApartment.Enter();
        nint shellItem = 0;
        try
        {
            Marshal.ThrowExceptionForHR(SHCreateItemInKnownFolder(
                FOLDERID_AppsFolder,
                KF_FLAG_DONT_VERIFY,
                appId,
                ShellItem2InterfaceId,
                out shellItem));

            return (
                GetStringProperty(shellItem, PropertyKeys.PKEY_ItemNameDisplay),
                GetStringProperty(shellItem, PropertyKeys.PKEY_Link_TargetParsingPath));
        }
        finally
        {
            Release(shellItem);
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemInKnownFolder(
        in Guid knownFolderId,
        uint knownFolderFlags,
        string item,
        in Guid interfaceId,
        out nint shellItem);

    [LibraryImport("ole32.dll")]
    private static partial void CoTaskMemFree(nint value);

    private static nint GetMethod(nint instance, int slot)
    {
        return (*(nint**)instance)[slot];
    }

    private static string GetStringProperty(nint shellItem, in PROPERTYKEY key)
    {
        nint value = 0;
        try
        {
            var getString = (delegate* unmanaged[Stdcall]<nint, PROPERTYKEY*, nint*, int>)GetMethod(shellItem, GetStringSlot);
            var propertyKey = key;
            Marshal.ThrowExceptionForHR(getString(shellItem, &propertyKey, &value));
            return Marshal.PtrToStringUni(value) ?? string.Empty;
        }
        finally
        {
            CoTaskMemFree(value);
        }
    }

    private static void Release(nint instance)
    {
        if (instance == 0)
        {
            return;
        }

        var release = (delegate* unmanaged[Stdcall]<nint, uint>)GetMethod(instance, ReleaseSlot);
        _ = release(instance);
    }

}
