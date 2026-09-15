namespace JPSoftworks.MediaControlsExtension.Helpers;

internal static class WindowsMediaBackendDefaults
{
    private const string InternalKey = "jpsoftworks.mediacontrols.MediaBackends.gsmtc.Enabled";
    private const string WorkerKey = "jpsoftworks.mediacontrols.MediaBackends.gsmtc.worker.Enabled";

    public static SettingsReadResult Normalize(SettingsReadResult result)
    {
        if (result.Values is { } saved && !saved.ContainsKey(WorkerKey) && saved.TryGetPropertyValue(InternalKey, out var enabled))
        {
            // The legacy switch selected Windows media before hosting modes existed.
            saved[WorkerKey] = enabled?.DeepClone();
            saved.Remove(InternalKey);
        }

        return result;
    }
}