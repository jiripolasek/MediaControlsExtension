using JPSoftworks.MediaControlsExtension.MediaHost.Backends;

namespace JPSoftworks.MediaControlsExtension.MediaHost;

internal static class WorkerBackendCatalog
{
    public static IReadOnlyDictionary<string, WorkerBackendFactory> Factories { get; } =
        new Dictionary<string, WorkerBackendFactory>(StringComparer.Ordinal)
        {
            ["gsmtc"] = GsmtcBackendFactory.Create,
            ["dummy"] = DummyBackendFactory.Create,
        };
}