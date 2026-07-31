[CmdletBinding()]
param(
    [Parameter()]
    [string] $ThemeRoot = (Join-Path $PSScriptRoot '..\Assets\IconThemes'),

    [Parameter()]
    [switch] $RequireComplete
)

$ErrorActionPreference = 'Stop'

$requiredIcons = @(
    'PlayPause',
    'Play',
    'Pause',
    'SkipNext',
    'SkipNext.disabled',
    'SkipPrevious',
    'SkipPrevious.disabled',
    'NoMedia',
    'ToggleMute',
    'VolumeUp',
    'VolumeDown',
    'VolumeMute',
    'VolumeZero',
    'VolumeUnmute',
    'VolumeLow',
    'VolumeMedium',
    'VolumeHigh'
)

$knownIcons = @(
    'PlayPause',
    'Play',
    'Pause',
    'SkipNext',
    'SkipPrevious',
    'NoMedia',
    'ToggleMute',
    'VolumeUp',
    'VolumeDown',
    'VolumeMute',
    'VolumeZero',
    'VolumeUnmute',
    'VolumeLow',
    'VolumeMedium',
    'VolumeHigh'
)

$resolvedRoot = Resolve-Path -LiteralPath $ThemeRoot -ErrorAction Stop
$hasErrors = $false
$hasMissingAssets = $false

foreach ($themeDirectory in Get-ChildItem -LiteralPath $resolvedRoot -Directory | Sort-Object Name) {
    $entries = @{}
    $themeHasErrors = $false

    $assets = Get-ChildItem -LiteralPath $themeDirectory.FullName -File |
        Where-Object Extension -in '.svg', '.png' |
        Sort-Object Name

    foreach ($asset in $assets) {
        if ($asset.Name -notmatch '^(?<icon>[A-Z][A-Za-z0-9]*)(?:\.(?<state>disabled))?(?:\.(?<appearance>light|dark))?\.(?<format>svg|png)$') {
            Write-Error "[$($themeDirectory.Name)] Malformed asset name: $($asset.Name)" -ErrorAction Continue
            $themeHasErrors = $true
            continue
        }

        $icon = $Matches.icon
        if ($knownIcons -cnotcontains $icon) {
            Write-Error "[$($themeDirectory.Name)] Unknown semantic icon '$icon': $($asset.Name)" -ErrorAction Continue
            $themeHasErrors = $true
            continue
        }

        $state = if ($Matches.state) { ".$($Matches.state)" } else { '' }
        $key = "$icon$state"
        if (-not $entries.ContainsKey($key)) {
            $entries[$key] = [ordered]@{
                Universal = $false
                Light = $false
                Dark = $false
            }
        }

        $variant = if ($Matches.appearance -eq 'light') {
            'Light'
        }
        elseif ($Matches.appearance -eq 'dark') {
            'Dark'
        }
        else {
            'Universal'
        }

        if ($entries[$key][$variant]) {
            Write-Error "[$($themeDirectory.Name)] '$key.$($variant.ToLowerInvariant())' has both SVG and PNG files; SVG would take precedence." -ErrorAction Continue
            $themeHasErrors = $true
            continue
        }

        $entries[$key][$variant] = $true
    }

    foreach ($entry in $entries.GetEnumerator() | Sort-Object Key) {
        if (-not $entry.Value.Universal -and ($entry.Value.Light -xor $entry.Value.Dark)) {
            Write-Error "[$($themeDirectory.Name)] '$($entry.Key)' has only one appearance variant and no universal asset." -ErrorAction Continue
            $themeHasErrors = $true
        }
    }

    $missing = foreach ($requiredIcon in $requiredIcons) {
        if (-not $entries.ContainsKey($requiredIcon)) {
            $requiredIcon
            continue
        }

        $variants = $entries[$requiredIcon]
        if (-not $variants.Universal -and -not ($variants.Light -and $variants.Dark)) {
            $requiredIcon
        }
    }

    if ($missing.Count -gt 0) {
        $hasMissingAssets = $true
        Write-Warning "[$($themeDirectory.Name)] Missing $($missing.Count) asset entries (theme glyphs or runtime fallback may provide them): $($missing -join ', ')"
    }

    if ($themeHasErrors) {
        $hasErrors = $true
    }
    else {
        Write-Host "[$($themeDirectory.Name)] Convention is valid; $($entries.Count) semantic icon/state entries found."
    }
}

if ($hasErrors -or ($RequireComplete -and $hasMissingAssets)) {
    exit 1
}

if ($hasMissingAssets) {
    Write-Host 'Icon theme validation completed with glyph- or fallback-backed asset gaps.'
}
else {
    Write-Host 'All asset-backed icon themes are complete.'
}
