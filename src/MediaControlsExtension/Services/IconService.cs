// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Services;

internal sealed partial class IconService : IIconService
{
    private readonly SettingsManager _settingsManager;
    private readonly Lock _lock = new();
    private readonly Dictionary<IconCacheKey, IconInfo> _cache = [];
    private readonly List<IconThemeDiagnostic> _diagnostics = [];
    private readonly HashSet<string> _reportedDiagnostics = new(StringComparer.Ordinal);

    private string _commandPaletteThemeId;
    private string _dockThemeId;
    private bool _disposed;

    public IconService(SettingsManager settingsManager)
    {
        ArgumentNullException.ThrowIfNull(settingsManager);

        this._settingsManager = settingsManager;
        this._commandPaletteThemeId = settingsManager.CommandPaletteIconThemeId;
        this._dockThemeId = settingsManager.DockIconThemeId;

        ValidateFallbackTheme();
        foreach (var theme in IconThemeCatalog.Themes)
        {
            var definition = IconThemeCatalog.GetThemeOrDefault(theme.Id);
            if (definition.AssetDirectory is not null)
            {
                this.ValidateAssetConvention(definition);
            }
        }

        this._settingsManager.Settings.SettingsChanged += this.SettingsOnSettingsChanged;
    }

    public event EventHandler? IconsChanged;

    public IReadOnlyList<IconThemeInfo> Themes => IconThemeCatalog.Themes;

    public IReadOnlyList<IconThemeDiagnostic> Diagnostics
    {
        get
        {
            lock (this._lock)
            {
                return this._diagnostics.ToArray();
            }
        }
    }

    public IconInfo GetIcon(
        ThemedIcon icon,
        IconSurface surface,
        IconState state = IconState.Default)
    {
        var themeId = surface == IconSurface.Dock
            ? this._settingsManager.DockIconThemeId
            : this._settingsManager.CommandPaletteIconThemeId;
        var theme = IconThemeCatalog.GetThemeOrDefault(themeId);
        var request = new IconRequest(icon, state);
        var cacheKey = new IconCacheKey(theme.Info.Id, request);

        lock (this._lock)
        {
            ObjectDisposedException.ThrowIf(this._disposed, this);

            if (this._cache.TryGetValue(cacheKey, out var cachedIcon))
            {
                return cachedIcon;
            }

            if (TryCreateIcon(theme, request, out var resolvedIcon))
            {
                this._cache.Add(cacheKey, resolvedIcon);
                return resolvedIcon;
            }

            var fallbackTheme = IconThemeCatalog.FallbackTheme;
            if (!TryCreateIcon(fallbackTheme, request, out resolvedIcon))
            {
                throw new InvalidOperationException(
                    $"Fallback icon theme '{fallbackTheme.Info.Id}' does not define '{FormatRequest(request)}'.");
            }

            this.ReportDiagnostic(
                theme.Info.Id,
                $"Icon '{FormatRequest(request)}' is missing; using fallback theme '{fallbackTheme.Info.DisplayName}'.");
            this._cache.Add(cacheKey, resolvedIcon);
            return resolvedIcon;
        }
    }

    public void Dispose()
    {
        lock (this._lock)
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            this._settingsManager.Settings.SettingsChanged -= this.SettingsOnSettingsChanged;
            this.IconsChanged = null;
        }
    }

    private static bool TryCreateIcon(
        IconThemeDefinition theme,
        IconRequest request,
        out IconInfo icon)
    {
        if (theme.AssetDirectory is not null &&
            TryResolveAssetPair(theme.AssetDirectory, request, out var lightPath, out var darkPath))
        {
            icon = IconHelpers.FromRelativePaths(lightPath, darkPath);
            return true;
        }

        if (theme.Glyphs?.TryGetValue(request, out var glyph) == true)
        {
            icon = new(glyph);
            return true;
        }

        icon = null!;
        return false;
    }

    private static bool TryResolveAssetPair(
        string assetDirectory,
        IconRequest request,
        out string lightPath,
        out string darkPath)
    {
        var baseName = request.State == IconState.Default
            ? request.Icon.ToString()
            : $"{request.Icon}.{request.State.ToString().ToLowerInvariant()}";
        var hasUniversal = TryResolveAsset(assetDirectory, baseName, out var universalPath);
        var hasLightOverride = TryResolveAsset(assetDirectory, $"{baseName}.light", out var lightOverridePath);
        var hasDarkOverride = TryResolveAsset(assetDirectory, $"{baseName}.dark", out var darkOverridePath);

        lightPath = hasLightOverride ? lightOverridePath : universalPath;
        darkPath = hasDarkOverride ? darkOverridePath : universalPath;

        return (hasLightOverride || hasUniversal) && (hasDarkOverride || hasUniversal);
    }

    private static bool TryResolveAsset(
        string assetDirectory,
        string fileStem,
        out string path)
    {
        foreach (var extension in IconAssetConvention.SupportedExtensions)
        {
            var candidate = Path.Combine(assetDirectory, $"{fileStem}{extension}");
            if (AssetExists(candidate))
            {
                path = candidate;
                return true;
            }
        }

        path = string.Empty;
        return false;
    }

    private static void ValidateFallbackTheme()
    {
        var fallbackTheme = IconThemeCatalog.FallbackTheme;
        foreach (var request in IconThemeCatalog.RequiredIcons)
        {
            if (fallbackTheme.Glyphs?.ContainsKey(request) != true)
            {
                throw new InvalidOperationException(
                    $"Fallback icon theme '{fallbackTheme.Info.Id}' does not define '{FormatRequest(request)}'.");
            }
        }
    }

    private void ValidateAssetConvention(IconThemeDefinition theme)
    {
        var absoluteDirectory = ResolveAssetPath(theme.AssetDirectory!);
        if (!Directory.Exists(absoluteDirectory))
        {
            this.ReportDiagnostic(
                theme.Info.Id,
                $"Icon theme directory '{theme.AssetDirectory}' does not exist.");
            return;
        }

        var assetStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(absoluteDirectory, "*.*", SearchOption.TopDirectoryOnly)
                     .Where(static path => IconAssetConvention.IsSupportedExtension(Path.GetExtension(path))))
        {
            if (!IconAssetConvention.TryParse(Path.GetFileName(path), out _))
            {
                this.ReportDiagnostic(
                    theme.Info.Id,
                    $"Asset '{Path.GetFileName(path)}' does not match Icon[.state][.light|.dark].(svg|png).");
                continue;
            }

            if (!assetStems.Add(Path.GetFileNameWithoutExtension(path)))
            {
                this.ReportDiagnostic(
                    theme.Info.Id,
                    $"Asset slot '{Path.GetFileNameWithoutExtension(path)}' has both SVG and PNG files; SVG takes precedence.");
            }
        }
    }

    private void SettingsOnSettingsChanged(object sender, Settings args)
    {
        EventHandler? handler = null;
        lock (this._lock)
        {
            if (this._disposed)
            {
                return;
            }

            var commandPaletteThemeId = this._settingsManager.CommandPaletteIconThemeId;
            var dockThemeId = this._settingsManager.DockIconThemeId;
            if (string.Equals(commandPaletteThemeId, this._commandPaletteThemeId, StringComparison.Ordinal) &&
                string.Equals(dockThemeId, this._dockThemeId, StringComparison.Ordinal))
            {
                return;
            }

            this._commandPaletteThemeId = commandPaletteThemeId;
            this._dockThemeId = dockThemeId;
            handler = this.IconsChanged;
        }

        handler?.Invoke(this, EventArgs.Empty);
    }

    private void ReportDiagnostic(string themeId, string message)
    {
        var key = $"{themeId}\0{message}";
        if (!this._reportedDiagnostics.Add(key))
        {
            return;
        }

        this._diagnostics.Add(new(themeId, message));
        Logger.LogWarning($"Icon theme '{themeId}': {message}");
    }

    private static bool AssetExists(string relativePath)
        => File.Exists(ResolveAssetPath(relativePath));

    private static string ResolveAssetPath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        var packagedPath = Path.Combine(AppContext.BaseDirectory, path);
        return File.Exists(packagedPath) || Directory.Exists(packagedPath)
            ? packagedPath
            : Path.GetFullPath(path);
    }

    private static string FormatRequest(IconRequest request)
        => request.State == IconState.Default
            ? request.Icon.ToString()
            : $"{request.Icon}.{request.State.ToString().ToLowerInvariant()}";

    private readonly record struct IconCacheKey(
        string ThemeId,
        IconRequest Request);
}

internal static class IconAssetConvention
{
    public static IReadOnlyList<string> SupportedExtensions { get; } = (string[])[".svg", ".png"];

    public static bool IsSupportedExtension(string extension)
        => SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    public static bool TryParse(string fileName, out IconRequest request)
    {
        request = default;
        if (!IsSupportedExtension(Path.GetExtension(fileName)))
        {
            return false;
        }

        var parts = Path.GetFileNameWithoutExtension(fileName).Split('.');
        if (parts.Length is < 1 or > 3 ||
            !Enum.TryParse(parts[0], ignoreCase: false, out ThemedIcon icon))
        {
            return false;
        }

        var state = IconState.Default;
        var index = 1;
        if (index < parts.Length &&
            Enum.TryParse(parts[index], ignoreCase: true, out IconState parsedState) &&
            parsedState != IconState.Default)
        {
            state = parsedState;
            index++;
        }

        if (index < parts.Length &&
            !string.Equals(parts[index], "light", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(parts[index], "dark", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (parts.Length - index > 1)
        {
            return false;
        }

        request = new(icon, state);
        return true;
    }
}
