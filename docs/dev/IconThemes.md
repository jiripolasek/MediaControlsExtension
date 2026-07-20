# Icon themes

## Scope

`IIconService` resolves themeable icons by semantic ID, surface, and state:

```csharp
IconInfo GetIcon(ThemedIcon icon, IconSurface surface, IconState state = IconState.Default)
```

- `IconSurface.CommandPalette` covers the home and list pages.
- `IconSurface.Dock` covers dock items.
- The command palette and dock theme selections are stored independently.
- Context-menu icons, application and provider branding, package icons, and media thumbnails are not themed.
- External theme directories and archives are not supported yet.

## Asset convention

Store built-in theme assets under:

```text
src/MediaControlsExtension/Assets/IconThemes/<theme-id>/
```

Asset names use this grammar:

```text
<Icon>[.<State>][.<Appearance>].<Extension>
```

| Token | Allowed values | Notes |
|---|---|---|
| `Icon` | A `ThemedIcon` name | Case-sensitive PascalCase. |
| `State` | `disabled` | Optional. Omission means `IconState.Default`. State precedes appearance. |
| `Appearance` | `light`, `dark` | Optional. Omission defines a universal light/dark asset. |
| `Extension` | `svg`, `png` | Transparent PNG files are supported. |

Examples:

```text
Play.svg
SkipNext.disabled.dark.svg
VolumeHigh.light.png
```

Variant rules:

- A universal asset supplies both appearances.
- `light` or `dark` overrides the universal asset for that appearance.
- Without a universal asset, both appearance variants are required.
- SVG and PNG assets may be mixed within a theme.
- If SVG and PNG define the same icon/state/appearance slot, SVG wins and a diagnostic is emitted.

## Supported semantic icons

These are the recognized `ThemedIcon` values. The required request matrix includes `Default` for every icon and additionally includes `Disabled` for skip actions.

| Icon | Meaning | Required states |
|---|---|---|
| `PlayPause` | State-independent playback toggle | Default |
| `Play` | Start or resume playback | Default |
| `Pause` | Pause playback | Default |
| `SkipNext` | Skip to the next track | Default, Disabled |
| `SkipPrevious` | Skip to the previous track | Default, Disabled |
| `NoMedia` | No active media session | Default |
| `ToggleMute` | Toggle the mute state | Default |
| `VolumeUp` | Increase-volume action | Default |
| `VolumeDown` | Decrease-volume action | Default |
| `VolumeMute` | Mute action or muted-volume state | Default |
| `VolumeOff` | Unmute action or zero-volume state | Default |
| `VolumeLow` | Volume level from 1% through 33% | Default |
| `VolumeMedium` | Volume level from 34% through 66% | Default |
| `VolumeHigh` | Volume level from 67% through 100% | Default |

`IconState.Disabled` is supported by the filename parser for any semantic icon, but only the two skip-action slots are in the required matrix. A theme may omit a request when its glyph mapping or the fallback theme supplies it.

## Resolution and fallback

For each icon request, `IconService` resolves the first available source in this order:

1. Selected-theme asset pair.
2. Selected-theme glyph mapping.
3. Fallback-theme asset pair.
4. Fallback-theme glyph mapping.

The built-in fallback is `fluent-outline`. Missing selected-theme entries emit a diagnostic before fallback. A missing fallback entry is a startup or resolution error.

The settings value `default` resolves through `IconThemeCatalog.CurrentDefaultThemeId`, currently `colorful`. An explicit `colorful` selection remains pinned if the application default changes later.

## Built-in themes

| Theme ID | Display name | Selectable | Source | Description / role |
|---|---|---:|---|---|
| `colorful` | Colorful | Yes | Assets | Original multicolor set; current application default. |
| `fluent-outline` | Fluent Outline | Yes | Assets and Segoe Fluent glyphs | Outline system set; runtime fallback theme. |
| `fluent-solid` | Fluent Solid | Yes | Assets and Segoe Fluent glyphs | Filled Segoe Fluent set with asset overrides. |
| `pink-princess` | Pink Princess | Yes | Assets | Pink and pastel candy palette with sparkle accents. |
| `chubby-mono` | Chubby Mono | Yes | Assets | Rounded cartoon geometry with muted monochrome light/dark variants. |
| `chubby-colorful` | Chubby Colorful | Yes | Assets | Rounded cartoon geometry with a multicolor candy palette. |
| `neon-circuit` | Neon Circuit | Yes | Assets | Dark faceted forms with cyan, magenta, lime, gold, and directional status colors. |
| `eight-bit-blocks` | 8-Bit Blocks | No | Assets | Pixel-art prototype. Assets remain in the tree; catalog registration is commented out. |

Theme registration is defined in `Services/IconThemeCatalog.cs`. The project file includes `Assets/IconThemes/**/*` as content, so files below the theme root are copied to the build output automatically.

## Validation

Run the convention validator from the repository root:

```powershell
pwsh -File .\src\MediaControlsExtension\scripts\Test-IconThemes.ps1
```

Require every asset-backed directory to provide the complete required matrix:

```powershell
pwsh -File .\src\MediaControlsExtension\scripts\Test-IconThemes.ps1 -RequireComplete
```

The validator reports:

- malformed or unknown asset names;
- duplicate SVG/PNG slots;
- incomplete light/dark pairs;
- missing required icon/state entries.

Runtime diagnostics additionally report missing theme directories, malformed packaged assets, duplicate slots, and fallback use.
