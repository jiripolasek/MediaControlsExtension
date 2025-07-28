// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.System;

namespace JPSoftworks.MediaControlsExtension;

internal static class Chords
{
    private static KeyChord From(bool ctrl = false, bool alt = false, bool shift = false, bool win = false, VirtualKey vkey = 0, int scanCode = 0)
    {
        return KeyChordHelpers.FromModifiers(ctrl, alt, shift, win, (int)vkey, scanCode);
    }
    
    // media keyboard shortcuts follow Windows Media Player shortcuts

    public static KeyChord NextTrack { get; } = From(ctrl: true, vkey: VirtualKey.F);
    public static KeyChord PreviousTrack { get; } = From(ctrl: true, vkey: VirtualKey.B);
    public static KeyChord ToggleRepeat { get; } = From(ctrl: true, vkey: VirtualKey.T);
    public static KeyChord ToggleShuffle { get; } = From(ctrl: true, vkey: VirtualKey.H);
    public static KeyChord SwitchToApplication { get; } = From(ctrl: true, vkey: VirtualKey.G);
    
    public static KeyChord PreviousSession { get; } = From(ctrl: true, vkey: VirtualKey.J);
    public static KeyChord NextSession { get; } = From(ctrl: true, vkey: VirtualKey.N);
}