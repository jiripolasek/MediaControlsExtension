param(
    [ValidatePattern('^[a-p]{32}$')]
    [string[]]$ExtensionId,
    [ValidateSet('Edge', 'Chrome', 'Both')]
    [string]$Browser = 'Edge',
    [switch]$Unregister,
    [string]$HostDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $repoRoot 'src\BrowserCompanion\setup\Registration.psm1') -Force
if (-not $HostDirectory) { $HostDirectory = Join-Path $repoRoot 'artifacts\BrowserCompanion\host' }

if ($Unregister) {
    Remove-CompanionRegistration -HostDirectory $HostDirectory -Browser $Browser
    return
}

if (-not $ExtensionId) { throw 'Specify at least one extension ID.' }
dotnet publish (Join-Path $repoRoot 'src\BrowserCompanion\host\JPSoftworks.MediaControlsExtension.BrowserCompanion.csproj') -c Release --no-self-contained -o $HostDirectory
if ($LASTEXITCODE -ne 0) { throw 'Native host publish failed.' }

Set-CompanionRegistration -HostDirectory $HostDirectory -ExtensionId $ExtensionId -Browser $Browser
