namespace JPSoftworks.MediaControlsExtension.Helpers;

internal static class QueryCommandProcessorExtensions
{
    public static QueryCommandProcessor CreateProcessor(
        this IEnumerable<CommandMapping> mappings,
        QueryProcessorOptions? options = null) =>
        new(mappings.ToList().AsReadOnly(), options);

    public static QueryCommandProcessor CreateProcessor(
        this IEnumerable<CommandMapping> mappings,
        Action<QueryProcessorOptions> configure)
    {
        var options = new QueryProcessorOptions();
        configure(options);
        return mappings.CreateProcessor(options);
    }
}