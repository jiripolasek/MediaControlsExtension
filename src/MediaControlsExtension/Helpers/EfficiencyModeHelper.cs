// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace JPSoftworks.MediaControlsExtension.Helpers;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public static partial class EfficiencyModeHelper
{
    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessInformation(
        IntPtr hProcess,
        int ProcessInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE ProcessInformation,
        int ProcessInformationSize);

    /// <summary>
    /// Enables Windows Efficiency Mode (EcoQoS) for the current process.
    /// Throws an exception on failure.
    /// </summary>
    private static void EnableProcessEfficiencyMode()
    {
        if (!IsSupported())
            throw new PlatformNotSupportedException("Efficiency Mode (EcoQoS) is supported on Windows 11 (build 22000+) only.");

        var process = GetCurrentProcess();
        var ecoQosState = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = 1,
            ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED
        };

        var success = SetProcessInformation(
            process,
            ProcessPowerThrottling,
            ref ecoQosState,
            Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());

        if (!success)
        {
            var win32Error = Marshal.GetLastWin32Error();
            throw new Win32Exception(win32Error, "Failed to enable Efficiency Mode (EcoQoS) for process.");
        }
    }

    public static bool TryEnableProcessEfficiencyMode()
    {
        try
        {
            EnableProcessEfficiencyMode();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true if the current OS is Windows 11 (build 22000) or newer.
    /// </summary>
    private static bool IsSupported()
    {
        return Environment.OSVersion is { Platform: PlatformID.Win32NT, Version: { Major: >= 10, Build: >= 22000 } };
    }
}