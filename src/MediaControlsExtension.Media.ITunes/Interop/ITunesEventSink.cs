// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace JPSoftworks.MediaControlsExtension.Media.ITunes.Interop;

internal sealed unsafe class ITunesEventSink : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct IDispatchVtbl
    {
        public delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int> QueryInterface;
        public delegate* unmanaged[Stdcall]<nint, uint> AddRef;
        public delegate* unmanaged[Stdcall]<nint, uint> Release;
        public delegate* unmanaged[Stdcall]<nint, uint*, int> GetTypeInfoCount;
        public delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int> GetTypeInfo;
        public delegate* unmanaged[Stdcall]<nint, Guid*, nint, uint, uint, nint, int> GetIDsOfNames;
        public delegate* unmanaged[Stdcall]<nint, int, Guid*, uint, ushort, nint, nint, nint, uint*, int> Invoke;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ComInstance
    {
        public IDispatchVtbl* Vtbl;
        public nint GcHandle;
    }

    private static readonly nint StaticVtblPtr;

    static ITunesEventSink()
    {
        var vtbl = (IDispatchVtbl*)RuntimeHelpers.AllocateTypeAssociatedMemory(
            typeof(ITunesEventSink),
            sizeof(IDispatchVtbl));
        vtbl->QueryInterface = &QueryInterfaceImpl;
        vtbl->AddRef = &AddRefImpl;
        vtbl->Release = &ReleaseImpl;
        vtbl->GetTypeInfoCount = &GetTypeInfoCountImpl;
        vtbl->GetTypeInfo = &GetTypeInfoImpl;
        vtbl->GetIDsOfNames = &GetIDsOfNamesImpl;
        vtbl->Invoke = &InvokeImpl;
        StaticVtblPtr = (nint)vtbl;
    }

    private Action<int>? _onEvent;
    private nint _instance;
    private int _refCount = 1;
    private volatile bool _isDetached;

    public ITunesEventSink(Action<int> onEvent)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        this._onEvent = onEvent;

        var handle = GCHandle.Alloc(this, GCHandleType.Normal);
        var inst = (ComInstance*)NativeMemory.Alloc((nuint)sizeof(ComInstance));
        inst->Vtbl = (IDispatchVtbl*)StaticVtblPtr;
        inst->GcHandle = GCHandle.ToIntPtr(handle);
        this._instance = (nint)inst;
    }

    public nint IUnknownPointer => this._instance;

    public bool IsDetached => this._isDetached;

    public void Detach()
    {
        this._isDetached = true;
        _ = Interlocked.Exchange(ref this._onEvent, null);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterfaceImpl(nint thisPtr, Guid* riid, nint* ppv)
    {
        if (ppv == null || riid == null)
        {
            return unchecked((int)0x80070057); // E_INVALIDARG
        }

        if (*riid == ITunesGuids.IidIUnknown ||
            *riid == ITunesGuids.IidIDispatch ||
            *riid == ITunesGuids.DiidIiTunesEvents)
        {
            *ppv = thisPtr;
            AddRefCore((ComInstance*)thisPtr);
            return 0; // S_OK
        }

        *ppv = 0;
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    private static uint AddRefCore(ComInstance* inst)
    {
        if (inst == null)
        {
            return 0;
        }

        var gcHandle = GCHandle.FromIntPtr(inst->GcHandle);
        if (gcHandle.IsAllocated && gcHandle.Target is ITunesEventSink sink)
        {
            return (uint)Interlocked.Increment(ref sink._refCount);
        }

        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRefImpl(nint thisPtr)
    {
        return AddRefCore((ComInstance*)thisPtr);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ReleaseImpl(nint thisPtr)
    {
        return ReleaseCore((ComInstance*)thisPtr);
    }

    private static uint ReleaseCore(ComInstance* inst)
    {
        if (inst == null)
        {
            return 0;
        }

        var gcHandle = GCHandle.FromIntPtr(inst->GcHandle);
        if (gcHandle.IsAllocated && gcHandle.Target is ITunesEventSink sink)
        {
            var newCount = Interlocked.Decrement(ref sink._refCount);
            if (newCount == 0)
            {
                gcHandle.Free();
                NativeMemory.Free(inst);
                return 0;
            }

            return (uint)newCount;
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetTypeInfoCountImpl(nint thisPtr, uint* pctinfo)
    {
        if (pctinfo != null)
        {
            *pctinfo = 0;
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetTypeInfoImpl(nint thisPtr, uint iTInfo, uint lcid, nint* ppTInfo)
    {
        if (ppTInfo != null)
        {
            *ppTInfo = 0;
        }

        return unchecked((int)0x80004001); // E_NOTIMPL
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetIDsOfNamesImpl(nint thisPtr, Guid* riid, nint rgszNames, uint cNames, uint lcid, nint rgDispId)
    {
        return unchecked((int)0x80004001); // E_NOTIMPL
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int InvokeImpl(nint thisPtr, int dispIdMember, Guid* riid, uint lcid, ushort wFlags, nint pDispParams, nint pVarResult, nint pExcepInfo, uint* puArgErr)
    {
        var inst = (ComInstance*)thisPtr;
        if (inst == null)
        {
            return 0;
        }

        var gcHandle = GCHandle.FromIntPtr(inst->GcHandle);
        if (gcHandle.IsAllocated && gcHandle.Target is ITunesEventSink sink)
        {
            var callback = Volatile.Read(ref sink._onEvent);
            if (callback != null && !sink._isDetached)
            {
                try
                {
                    callback(dispIdMember);
                }
                catch
                {
                    // Do not leak exceptions to native callers
                }
            }
        }

        return 0; // S_OK
    }

    public void Dispose()
    {
        this.Detach();
        var inst = (ComInstance*)Interlocked.Exchange(ref this._instance, 0);
        if (inst != null)
        {
            _ = ReleaseCore(inst);
        }
    }
}
