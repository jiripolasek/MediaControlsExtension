// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Runtime.InteropServices;

namespace JPSoftworks.MediaControlsExtension.Interop;

internal static partial class ComApartment
{
    private const uint CoInitMultithreaded = 0;
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    public static Scope Enter()
    {
        var result = CoInitializeEx(0, CoInitMultithreaded);
        if (result == RpcEChangedMode)
        {
            // COM is already initialized as an STA. The caller can use that
            // apartment, but this scope must not uninitialize it.
            return default;
        }

        Marshal.ThrowExceptionForHR(result);
        return new(ownsInitialization: true);
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint concurrencyModel);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    internal readonly ref struct Scope
    {
        private readonly bool _ownsInitialization;

        internal Scope(bool ownsInitialization)
        {
            this._ownsInitialization = ownsInitialization;
        }

        public void Dispose()
        {
            if (this._ownsInitialization)
            {
                CoUninitialize();
            }
        }
    }
}
