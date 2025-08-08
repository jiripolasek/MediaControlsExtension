namespace JPSoftworks.MediaControlsExtension.Helpers;

internal sealed class QueryCommandProcessor(
    IReadOnlyCollection<CommandMapping> commandMappings,
    QueryProcessorOptions? options = null)
{
    private readonly IReadOnlyCollection<CommandMapping> _commandMappings = commandMappings
                                                                            ?? throw new ArgumentNullException(nameof(commandMappings));
    private readonly QueryProcessorOptions _options = options ?? new();

    public string ProcessQuery(string query) =>
        string.IsNullOrWhiteSpace(query)
            ? string.Empty
            : this.ProcessNonEmptyQuery(query);

    private string ProcessNonEmptyQuery(string query)
    {
        var (processedQuery, hadSlashPrefix) = this.RemoveSlashPrefix(query);
        var matchedMapping = this.FindMatchingCommand(processedQuery);

        return matchedMapping switch
        {
            null => this._options.FallbackCommandName,
            _ => this.FormatCommandName(processedQuery, matchedMapping, hadSlashPrefix)
        };
    }

    private (string processedQuery, bool hadSlashPrefix) RemoveSlashPrefix(string query)
    {
        var hadSlashPrefix = this._options.SupportSlashPrefix && query.StartsWith('/');
        return hadSlashPrefix ? (query[1..], true) : (query, false);
    }

    private CommandMapping? FindMatchingCommand(string query) =>
        this._commandMappings
            .Where(mapping => query.StartsWith(mapping.Prefix, this._options.ComparisonType))
            .MaxBy(static mapping => mapping.Prefix.Length);

    private string FormatCommandName(string processedQuery, CommandMapping mapping, bool hadSlashPrefix)
    {
        var commandName = this._options.PreserveInputCasing
            ? ApplyCasing(processedQuery[..mapping.Prefix.Length], mapping.CommandName)
            : mapping.CommandName;

        return hadSlashPrefix && this._options.SupportSlashPrefix && !string.IsNullOrWhiteSpace(commandName)
            ? $"/{commandName}"
            : commandName;
    }

    private static string ApplyCasing(string inputPattern, string targetText) =>
        inputPattern switch
        {
            "" => targetText,
            _ when IsAllUpperCase(inputPattern) => targetText.ToUpperInvariant(),
            _ when IsAllLowerCase(inputPattern) => targetText.ToLowerInvariant(),
            _ when IsTitleCase(inputPattern) => targetText,
            _ => targetText
        };

    private static bool IsAllUpperCase(string text)
    {
        foreach (var c in text)
        {
            if (!char.IsUpper(c))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsAllLowerCase(string text)
    {
        foreach (var c in text)
        {
            if (!char.IsLower(c))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsTitleCase(string text) =>
        text.Length > 0 && char.IsUpper(text[0]) && text[1..].All(char.IsLower);
}