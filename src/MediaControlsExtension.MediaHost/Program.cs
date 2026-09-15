namespace JPSoftworks.MediaControlsExtension.MediaHost;

internal static class Program
{
    [MTAThread]
    public static async Task Main(string[] args)
    {
        var result = await WorkerApplication.RunAsync(args, WorkerBackendCatalog.Factories).ConfigureAwait(false);
        Environment.Exit(result);
    }
}