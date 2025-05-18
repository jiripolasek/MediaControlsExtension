using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace JPSoftworks.MediaControlsExtension.Interop;

[SuppressMessage("ReSharper", "InconsistentNaming")]
internal static class NativeMethods
{
    internal static readonly Guid FOLDERID_AppsFolder = Guid.Parse("{1e87508d-89c2-42f0-8a7e-645a0f50ca58}");

    internal const int KF_FLAG_DONT_VERIFY = 0x00004000;

    internal const int WAIT_TIMEOUT = 0x00000102;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    [return: MarshalAs(UnmanagedType.Interface)]
    internal static extern IShellItem2 SHCreateItemInKnownFolder(
        [MarshalAs(UnmanagedType.LPStruct)] Guid kfid,
        uint dwKFFlags,
        [MarshalAs(UnmanagedType.LPWStr)] string pszItem,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid);

}