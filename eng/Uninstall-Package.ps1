<#
.SYNOPSIS
Uninstalls the configured WAP package for the current user.

.DESCRIPTION
Reads the package identity from the manifest configured in
Package.config.psd1 and removes matching packages for the current user.
Generated AppX layout files are not deleted.

.EXAMPLE
.\eng\Uninstall-Package.ps1

.EXAMPLE
.\eng\Uninstall-Package.ps1 -PreserveApplicationData

.EXAMPLE
.\eng\Uninstall-Package.ps1 -WhatIf
#>

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [Parameter()]
    [switch] $PreserveApplicationData,

    [Parameter()]
    [string] $ConfigPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$previousWhatIfPreference = $WhatIfPreference
try {
    $WhatIfPreference = $false
    Import-Module Appx -ErrorAction Stop
}
finally {
    $WhatIfPreference = $previousWhatIfPreference
}

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $PSScriptRoot 'Package.config.psd1'
}

$ConfigPath = [IO.Path]::GetFullPath($ConfigPath)
if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    throw "WAP package configuration was not found: '$ConfigPath'."
}

$packageConfig = Import-PowerShellDataFile -LiteralPath $ConfigPath
foreach ($requiredConfigKey in @('RepositoryRoot', 'PackageManifestPath')) {
    if (-not $packageConfig.ContainsKey($requiredConfigKey)) {
        throw "WAP package configuration '$ConfigPath' is missing '$requiredConfigKey'."
    }
}

$configDirectory = Split-Path -Parent $ConfigPath
$repoRoot = [IO.Path]::GetFullPath((Join-Path $configDirectory ([string] $packageConfig.RepositoryRoot)))
$configuredManifestPath = [string] $packageConfig.PackageManifestPath
$packageManifestPath = if ([IO.Path]::IsPathRooted($configuredManifestPath)) {
    [IO.Path]::GetFullPath($configuredManifestPath)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $configuredManifestPath))
}

if (-not (Test-Path -LiteralPath $packageManifestPath -PathType Leaf)) {
    throw "Configured package manifest was not found: '$packageManifestPath'."
}

[xml] $packageManifest = Get-Content -LiteralPath $packageManifestPath -Raw
$packageIdentityName = [string] $packageManifest.Package.Identity.Name
if ([string]::IsNullOrWhiteSpace($packageIdentityName)) {
    throw "Package identity name could not be read from '$packageManifestPath'."
}

$installedPackages = @(Get-AppxPackage -Name $packageIdentityName)
if ($installedPackages.Count -eq 0) {
    Write-Host "Package '$packageIdentityName' is not installed for the current user."
    return
}

$removedPackageCount = 0
foreach ($installedPackage in $installedPackages) {
    $action = if ($PreserveApplicationData) {
        'Uninstall package while preserving application data'
    }
    else {
        'Uninstall package and application data'
    }

    if (-not $PSCmdlet.ShouldProcess($installedPackage.PackageFullName, $action)) {
        continue
    }

    $removeParameters = @{
        Package     = $installedPackage.PackageFullName
        ErrorAction = 'Stop'
    }
    if ($PreserveApplicationData) {
        $removeParameters.PreserveApplicationData = $true
    }

    Remove-AppxPackage @removeParameters
    $removedPackageCount++
}

if ($removedPackageCount -eq 0) {
    return
}

$remainingPackages = @(Get-AppxPackage -Name $packageIdentityName)
if ($remainingPackages.Count -ne 0) {
    $remainingNames = @($remainingPackages.PackageFullName) -join ', '
    throw "Package uninstall did not remove all matching packages: $remainingNames."
}

$dataDisposition = if ($PreserveApplicationData) {
    'application data preserved'
}
else {
    'application data removed'
}
Write-Host "Uninstalled '$packageIdentityName' ($dataDisposition)."
