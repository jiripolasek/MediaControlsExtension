using System.Runtime.InteropServices;

namespace JPSoftworks.MediaControlsExtension.Interop;

[StructLayout(LayoutKind.Sequential, Pack = 0)]
public struct PropArray
{
    internal uint cElems;
    internal IntPtr pElems;
}