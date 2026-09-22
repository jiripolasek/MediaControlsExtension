// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace JPSoftworks.MediaControlsExtension.Media.ITunes.Interop;

internal static unsafe partial class ITunesNative
{
    public const uint ClsCtxLocalServer = 0x4;
    private const int ReleaseSlot = 2;

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(
        in Guid classId,
        nint outer,
        uint classContext,
        in Guid interfaceId,
        out nint instance);

    public static nint GetMethod(nint instance, int slot)
    {
        return (*(nint**)instance)[slot];
    }

    public static uint Release(nint instance)
    {
        if (instance == 0)
        {
            return 0;
        }

        var release = (delegate* unmanaged[Stdcall]<nint, uint>)GetMethod(instance, ReleaseSlot);
        return release(instance);
    }

    public static int QueryInterface(nint instance, in Guid iid, out nint result)
    {
        fixed (Guid* pIid = &iid)
        fixed (nint* pResult = &result)
        {
            var qi = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)GetMethod(instance, 0);
            return qi(instance, pIid, pResult);
        }
    }

    // --- IiTunes Methods ---

    public static int BackTrack(nint instance)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int>)GetMethod(instance, 7);
        return fn(instance);
    }

    public static int NextTrack(nint instance)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int>)GetMethod(instance, 9);
        return fn(instance);
    }

    public static int Pause(nint instance)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int>)GetMethod(instance, 10);
        return fn(instance);
    }

    public static int Play(nint instance)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int>)GetMethod(instance, 11);
        return fn(instance);
    }

    public static int Stop(nint instance)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int>)GetMethod(instance, 17);
        return fn(instance);
    }

    public static int GetPlayerState(nint instance, out ITPlayerState state)
    {
        int rawState = 0;
        var fn = (delegate* unmanaged[Stdcall]<nint, int*, int>)GetMethod(instance, 39);
        var hr = fn(instance, &rawState);
        state = (ITPlayerState)rawState;
        return hr;
    }

    public static int GetPlayerPosition(nint instance, out int position)
    {
        fixed (int* pPos = &position)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, int*, int>)GetMethod(instance, 40);
            return fn(instance, pPos);
        }
    }

    public static int GetCurrentTrack(nint instance, out nint pTrack)
    {
        fixed (nint* ppTrack = &pTrack)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, nint*, int>)GetMethod(instance, 62);
            return fn(instance, ppTrack);
        }
    }

    public static int GetCurrentPlaylist(nint instance, out nint pPlaylist)
    {
        fixed (nint* ppPlaylist = &pPlaylist)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, nint*, int>)GetMethod(instance, 63);
            return fn(instance, ppPlaylist);
        }
    }

    // --- IITPlaylist Methods ---

    public static int GetPlaylistShuffle(nint instance, out bool shuffle)
    {
        short raw = 0;
        var fn = (delegate* unmanaged[Stdcall]<nint, short*, int>)GetMethod(instance, 22);
        var hr = fn(instance, &raw);
        shuffle = raw != 0;
        return hr;
    }

    public static int SetPlaylistShuffle(nint instance, bool shuffle)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, short, int>)GetMethod(instance, 23);
        return fn(instance, (short)(shuffle ? -1 : 0));
    }

    public static int GetPlaylistRepeat(nint instance, out ITPlaylistRepeatMode repeatMode)
    {
        int raw = 0;
        var fn = (delegate* unmanaged[Stdcall]<nint, int*, int>)GetMethod(instance, 25);
        var hr = fn(instance, &raw);
        repeatMode = (ITPlaylistRepeatMode)raw;
        return hr;
    }

    public static int SetPlaylistRepeat(nint instance, ITPlaylistRepeatMode repeatMode)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int, int>)GetMethod(instance, 26);
        return fn(instance, (int)repeatMode);
    }

    // --- IITTrack Methods ---

    public static int GetTrackName(nint instance, out string name)
    {
        nint bstr = 0;
        var fn = (delegate* unmanaged[Stdcall]<nint, nint*, int>)GetMethod(instance, 8);
        var hr = fn(instance, &bstr);
        name = ReadAndFreeBstr(bstr);
        return hr;
    }

    public static int GetTrackDatabaseId(nint instance, out int id)
    {
        fixed (int* pId = &id)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, int*, int>)GetMethod(instance, 14);
            return fn(instance, pId);
        }
    }

    public static int GetTrackAlbum(nint instance, out string album)
    {
        nint bstr = 0;
        var fn = (delegate* unmanaged[Stdcall]<nint, nint*, int>)GetMethod(instance, 20);
        var hr = fn(instance, &bstr);
        album = ReadAndFreeBstr(bstr);
        return hr;
    }

    public static int GetTrackArtist(nint instance, out string artist)
    {
        nint bstr = 0;
        var fn = (delegate* unmanaged[Stdcall]<nint, nint*, int>)GetMethod(instance, 22);
        var hr = fn(instance, &bstr);
        artist = ReadAndFreeBstr(bstr);
        return hr;
    }

    public static int GetTrackDuration(nint instance, out int duration)
    {
        fixed (int* pDuration = &duration)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, int*, int>)GetMethod(instance, 38);
            return fn(instance, pDuration);
        }
    }

    public static int GetTrackGenre(nint instance, out string genre)
    {
        nint bstr = 0;
        var fn = (delegate* unmanaged[Stdcall]<nint, nint*, int>)GetMethod(instance, 45);
        var hr = fn(instance, &bstr);
        genre = ReadAndFreeBstr(bstr);
        return hr;
    }

    public static int GetTrackCount(nint instance, out int count)
    {
        fixed (int* pCount = &count)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, int*, int>)GetMethod(instance, 63);
            return fn(instance, pCount);
        }
    }

    public static int GetTrackNumber(nint instance, out int number)
    {
        fixed (int* pNumber = &number)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, int*, int>)GetMethod(instance, 65);
            return fn(instance, pNumber);
        }
    }

    public static int GetTrackArtworkCollection(nint instance, out nint pArtworks)
    {
        fixed (nint* ppArtworks = &pArtworks)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, nint*, int>)GetMethod(instance, 71);
            return fn(instance, ppArtworks);
        }
    }

    // --- IITArtworkCollection Methods ---

    public static int GetArtworkItem(nint instance, int index, out nint pArtwork)
    {
        fixed (nint* ppArtwork = &pArtwork)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)GetMethod(instance, 8);
            return fn(instance, index, ppArtwork);
        }
    }

    // --- IITArtwork Methods ---

    public static int SaveArtworkToFile(nint instance, string filePath)
    {
        var bstr = Marshal.StringToBSTR(filePath);
        try
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, nint, int>)GetMethod(instance, 9);
            return fn(instance, bstr);
        }
        finally
        {
            Marshal.FreeBSTR(bstr);
        }
    }

    public static int GetArtworkFormat(nint instance, out ITArtworkFormat format)
    {
        int raw = 0;
        var fn = (delegate* unmanaged[Stdcall]<nint, int*, int>)GetMethod(instance, 10);
        var hr = fn(instance, &raw);
        format = (ITArtworkFormat)raw;
        return hr;
    }

    // --- Connection Points ---

    public static int FindConnectionPoint(nint pContainer, in Guid riid, out nint pCP)
    {
        fixed (Guid* pGuid = &riid)
        fixed (nint* ppCP = &pCP)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)GetMethod(pContainer, 4);
            return fn(pContainer, pGuid, ppCP);
        }
    }

    public static int Advise(nint pCP, nint pSink, out uint cookie)
    {
        fixed (uint* pCookie = &cookie)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)GetMethod(pCP, 5);
            return fn(pCP, pSink, pCookie);
        }
    }

    public static int Unadvise(nint pCP, uint cookie)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, uint, int>)GetMethod(pCP, 6);
        return fn(pCP, cookie);
    }

    public static int DispatchInvoke(nint instance, int dispId)
    {
        if (instance == 0)
        {
            return unchecked((int)0x80004003); // E_POINTER
        }

        var fn = (delegate* unmanaged[Stdcall]<nint, int, Guid*, uint, ushort, nint, nint, nint, uint*, int>)GetMethod(instance, 6);
        return fn(instance, dispId, null, 0, 1, 0, 0, 0, null);
    }

    private static string ReadAndFreeBstr(nint bstr)
    {
        if (bstr == 0)
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringBSTR(bstr) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeBSTR(bstr);
        }
    }
}
