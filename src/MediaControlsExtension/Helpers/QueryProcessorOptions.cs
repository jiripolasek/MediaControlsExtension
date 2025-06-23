namespace JPSoftworks.MediaControlsExtension.Helpers;

internal sealed record QueryProcessorOptions(
    bool SupportSlashPrefix = true,
    bool PreserveInputCasing = true,
    string FallbackCommandName = "",
    StringComparison ComparisonType = StringComparison.InvariantCultureIgnoreCase
);
