// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Services;

internal static class IconThemeCatalog
{
    public const string DefaultSelectionId = "default";
    public const string ColorfulThemeId = "colorful";
    public const string FallbackThemeId = "fluent-outline";
    public const string FluentSolidThemeId = "fluent-solid";
    public const string PinkPrincessThemeId = "pink-princess";
    public const string ChubbyMonoThemeId = "chubby-mono";
    public const string ChubbyColorfulThemeId = "chubby-colorful";
    public const string EightBitBlocksThemeId = "eight-bit-blocks";
    public const string NeonCircuitThemeId = "neon-circuit";

    public const string CurrentDefaultThemeId = ColorfulThemeId;

    private static readonly IconThemeDefinition[] s_themes =
    [
        IconThemeDefinition.FromAssets(
            ColorfulThemeId,
            "Colorful",
            @"Assets\IconThemes\colorful"),
        IconThemeDefinition.FromAssetsAndGlyphs(
            FallbackThemeId,
            "Fluent Outline",
            @"Assets\IconThemes\fluent-outline",
            CreateFluentOutlineIcons()),
        IconThemeDefinition.FromAssetsAndGlyphs(
            FluentSolidThemeId,
            "Fluent Solid",
            @"Assets\IconThemes\fluent-solid",
            CreateFluentSolidIcons()),
        IconThemeDefinition.FromAssets(
            PinkPrincessThemeId,
            "Pink Princess",
            @"Assets\IconThemes\pink-princess"),
        IconThemeDefinition.FromAssets(
            ChubbyMonoThemeId,
            "Chubby Mono",
            @"Assets\IconThemes\chubby-mono"),
        IconThemeDefinition.FromAssets(
            ChubbyColorfulThemeId,
            "Chubby Colorful",
            @"Assets\IconThemes\chubby-colorful"),
        // Temporarily hidden from the settings selectors while its assets remain available for iteration.
        // IconThemeDefinition.FromAssets(
        //     EightBitBlocksThemeId,
        //     "8-Bit Blocks",
        //     @"Assets\IconThemes\eight-bit-blocks"),
        IconThemeDefinition.FromAssets(
            NeonCircuitThemeId,
            "Neon Circuit",
            @"Assets\IconThemes\neon-circuit"),
    ];

    public static IReadOnlyList<IconThemeInfo> Themes { get; } =
        [.. s_themes.Select(static theme => theme.Info)];

    public static IReadOnlyList<IconRequest> RequiredIcons { get; } =
    [
        new(ThemedIcon.PlayPause),
        new(ThemedIcon.Play),
        new(ThemedIcon.Pause),
        new(ThemedIcon.SkipNext),
        new(ThemedIcon.SkipNext, IconState.Disabled),
        new(ThemedIcon.SkipPrevious),
        new(ThemedIcon.SkipPrevious, IconState.Disabled),
        new(ThemedIcon.NoMedia),
        new(ThemedIcon.ToggleMute),
        new(ThemedIcon.VolumeUp),
        new(ThemedIcon.VolumeDown),
        new(ThemedIcon.VolumeMute),
        new(ThemedIcon.VolumeOff),
        new(ThemedIcon.VolumeLow),
        new(ThemedIcon.VolumeMedium),
        new(ThemedIcon.VolumeHigh),
    ];

    public static IconThemeDefinition GetThemeOrDefault(string? id)
    {
        foreach (var theme in s_themes)
        {
            if (string.Equals(theme.Info.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return theme;
            }
        }

        return s_themes.Single(
            static theme => theme.Info.Id == CurrentDefaultThemeId);
    }

    public static IconThemeInfo DefaultThemeInfo =>
        GetThemeOrDefault(CurrentDefaultThemeId).Info;

    public static string ResolveSelection(string? selectionId)
        => string.IsNullOrWhiteSpace(selectionId) ||
            string.Equals(selectionId, DefaultSelectionId, StringComparison.OrdinalIgnoreCase)
                ? CurrentDefaultThemeId
                : GetThemeOrDefault(selectionId).Info.Id;

    public static IconThemeDefinition FallbackTheme =>
        GetThemeOrDefault(FallbackThemeId);

    private static Dictionary<IconRequest, string> CreateFluentOutlineIcons()
    {
        return new()
        {
            [new(ThemedIcon.PlayPause)] = "\uE768",
            [new(ThemedIcon.Play)] = "\uE768",
            [new(ThemedIcon.Pause)] = "\uE769",
            [new(ThemedIcon.SkipNext)] = "\uE893",
            [new(ThemedIcon.SkipNext, IconState.Disabled)] = "\uE893",
            [new(ThemedIcon.SkipPrevious)] = "\uE892",
            [new(ThemedIcon.SkipPrevious, IconState.Disabled)] = "\uE892",
            [new(ThemedIcon.NoMedia)] = "\uEC4F",
            [new(ThemedIcon.ToggleMute)] = SegoeFluentIconGlyphs.ToggleMute,
            [new(ThemedIcon.VolumeUp)] = SegoeFluentIconGlyphs.VolumeUp,
            [new(ThemedIcon.VolumeDown)] = SegoeFluentIconGlyphs.VolumeDown,
            [new(ThemedIcon.VolumeMute)] = SegoeFluentIconGlyphs.VolumeMute,
            [new(ThemedIcon.VolumeOff)] = SegoeFluentIconGlyphs.VolumeOff,
            [new(ThemedIcon.VolumeLow)] = SegoeFluentIconGlyphs.VolumeLow,
            [new(ThemedIcon.VolumeMedium)] = SegoeFluentIconGlyphs.VolumeMedium,
            [new(ThemedIcon.VolumeHigh)] = SegoeFluentIconGlyphs.VolumeHigh,
        };
    }

    private static Dictionary<IconRequest, string> CreateFluentSolidIcons()
    {
        return new()
        {
            [new(ThemedIcon.PlayPause)] = "\uF5B0",
            [new(ThemedIcon.Play)] = "\uF5B0",
            [new(ThemedIcon.Pause)] = "\uF8AE",
            [new(ThemedIcon.ToggleMute)] = SegoeFluentIconGlyphs.ToggleMute,
            [new(ThemedIcon.VolumeUp)] = SegoeFluentIconGlyphs.VolumeUp,
            [new(ThemedIcon.VolumeDown)] = SegoeFluentIconGlyphs.VolumeDown,
            [new(ThemedIcon.VolumeMute)] = SegoeFluentIconGlyphs.VolumeMute,
            [new(ThemedIcon.VolumeOff)] = SegoeFluentIconGlyphs.VolumeOff,
            [new(ThemedIcon.VolumeLow)] = SegoeFluentIconGlyphs.VolumeLow,
            [new(ThemedIcon.VolumeMedium)] = SegoeFluentIconGlyphs.VolumeMedium,
            [new(ThemedIcon.VolumeHigh)] = SegoeFluentIconGlyphs.VolumeHigh,
        };
    }
}

internal sealed class IconThemeDefinition
{
    private IconThemeDefinition(
        IconThemeInfo info,
        string? assetDirectory,
        IReadOnlyDictionary<IconRequest, string>? glyphs)
    {
        this.Info = info;
        this.AssetDirectory = assetDirectory;
        this.Glyphs = glyphs;
    }

    public IconThemeInfo Info { get; }

    public string? AssetDirectory { get; }

    public IReadOnlyDictionary<IconRequest, string>? Glyphs { get; }

    public static IconThemeDefinition FromAssets(
        string id,
        string displayName,
        string assetDirectory)
        => new(new(id, displayName), assetDirectory, null);

    public static IconThemeDefinition FromGlyphs(
        string id,
        string displayName,
        IReadOnlyDictionary<IconRequest, string> glyphs)
        => new(new(id, displayName), null, glyphs);

    public static IconThemeDefinition FromAssetsAndGlyphs(
        string id,
        string displayName,
        string assetDirectory,
        IReadOnlyDictionary<IconRequest, string> glyphs)
        => new(new(id, displayName), assetDirectory, glyphs);
}

internal readonly record struct IconRequest(
    ThemedIcon Icon,
    IconState State = IconState.Default);
