// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal sealed partial class MediaCommandResultFactory(ISettingsManager settingsManager)
{
    public ICommandResult Create(string? message)
    {
        var isShiftDown = KeyModifierHelper.IsShiftPressed();
        var keepOpen = settingsManager.KeepOpen;
        if (isShiftDown)
        {
            keepOpen = !keepOpen;
        }

        var result = keepOpen ? CommandResult.KeepOpen() : CommandResult.Dismiss();
        return settingsManager.ShowToastMessages && !string.IsNullOrWhiteSpace(message)
            ? CommandResult.ShowToast(new ToastArgs { Message = message, Result = result })
            : result;
    }

    [SuppressMessage("ReSharper", "InconsistentNaming", Justification = "Win32 names")]
    private static partial class KeyModifierHelper
    {
        private const int VK_SHIFT = 0x10;
        private const short KEY_PRESSED_MASK = unchecked((short)0x8000);

        [LibraryImport("user32.dll")]
        private static partial short GetAsyncKeyState(int vKey);

        /// <summary>
        /// Returns true if either Shift key is currently held down.
        /// </summary>
        public static bool IsShiftPressed()
        {
            return (GetAsyncKeyState(VK_SHIFT) & KEY_PRESSED_MASK) != 0;
        }
    }
}
