namespace JPSoftworks.MediaControlsExtension.Media.Hosting;

internal static class WorkerTimeouts
{
    public static TimeSpan ForRequest(MessageKind kind, TimeSpan command, TimeSpan observation, TimeSpan policy) => kind switch
    {
        MessageKind.Policy => policy,
        MessageKind.Artwork => observation,
        _ => command,
    };
}