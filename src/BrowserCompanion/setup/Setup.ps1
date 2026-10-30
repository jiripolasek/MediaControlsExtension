<#
.SYNOPSIS
Registers or removes this portable companion bundle for the current Windows user.
#>
[CmdletBinding()]
param(
    [ValidatePattern('^[a-p]{32}$')][string[]] $ExtensionId,
    [ValidateSet('Edge', 'Chrome', 'Both')][string] $Browser = 'Edge',
    [switch] $Unregister
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Registration.psm1') -Force
$hostDirectory = Join-Path $PSScriptRoot 'host'

if ($Unregister) {
    Remove-CompanionRegistration -HostDirectory $hostDirectory -Browser $Browser
    return
}

if (-not $ExtensionId) {
    Write-Host 'Keep this extracted folder in a permanent location before continuing.'
    Write-Host 'Open edge://extensions or chrome://extensions and enable Developer mode.'
    Write-Host "Choose Load unpacked and select: $(Join-Path $PSScriptRoot 'extension')"
    Write-Host 'Copy the extension ID shown there. Use a comma to separate IDs for multiple browsers or profiles.'
    $ExtensionId = @((Read-Host 'Extension ID') -split ',' | ForEach-Object { $_.Trim() })
}

Set-CompanionRegistration -HostDirectory $hostDirectory -ExtensionId $ExtensionId -Browser $Browser
Write-Host 'Open the companion popup and confirm Desktop service connected.'
Write-Host 'In Media Controls, enable Browser companion under Media sources. Keep Windows media enabled for fallback.'
