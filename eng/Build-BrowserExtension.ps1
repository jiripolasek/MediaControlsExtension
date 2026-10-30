[CmdletBinding()]
param(
    [ValidateSet('Chromium', 'Firefox')][string] $Browser = 'Chromium',
    [Parameter(Mandatory)][string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repoRoot 'src\BrowserCompanion\extension'
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new extension output directory.' }
$manifest = Get-Content -LiteralPath (Join-Path $source 'manifest.json') -Raw | ConvertFrom-Json
if ($Browser -eq 'Firefox') {
    $manifest.PSObject.Properties.Remove('minimum_chrome_version')
    $manifest.background = @{ scripts = @('background.js'); type = 'module' }
    $manifest | Add-Member browser_specific_settings @{
        gecko = @{
            id = 'media-controls-companion@jiripolasek.com'
            strict_min_version = '153.0'
            data_collection_permissions = @{ required = @('browsingActivity', 'websiteContent') }
        }
    }
}
[IO.Directory]::CreateDirectory((Join-Path $output 'icons')) | Out-Null
$files = @('background.js', 'content.js', 'page.js', 'routing.mjs', 'grouping.mjs', 'popup.html', 'popup.js', 'popup.css')
$files += @($manifest.icons.PSObject.Properties.Value)
foreach ($file in $files) { Copy-Item -LiteralPath (Join-Path $source $file) -Destination (Join-Path $output $file) }
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE.txt') -Destination $output
[IO.File]::WriteAllText((Join-Path $output 'manifest.json'), ($manifest | ConvertTo-Json -Depth 12) + "`r`n", [Text.UTF8Encoding]::new($false))
Write-Output $output
