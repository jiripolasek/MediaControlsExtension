using System.Runtime.InteropServices;

namespace JPSoftworks.MediaControlsExtension.Interop;

[ComImport]
[Guid("DE25675A-72DE-44b4-9373-05170450C140")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IApplicationResolver
{
    void _();
    void __();
    void ___();
    void GetAppIDForProcess(uint processId, [MarshalAs(UnmanagedType.LPWStr)] out string appId, out IntPtr _, out IntPtr __, out IntPtr ____);
}