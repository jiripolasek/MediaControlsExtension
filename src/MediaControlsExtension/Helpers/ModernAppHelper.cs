using System.Runtime.InteropServices;
using Windows.ApplicationModel;
using JPSoftworks.MediaControlsExtension.Interop;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal static class ModernAppHelper
{
    public static AppInfo? Get(string appUserModelId)
    {
        try
        {
            return AppInfo.GetFromAppUserModelId(appUserModelId);
        }
        catch (COMException ex) when ((uint)ex.ErrorCode == (uint)HRESULT.ERROR_NOT_FOUND)
        {
            return null;
        }
    }
}