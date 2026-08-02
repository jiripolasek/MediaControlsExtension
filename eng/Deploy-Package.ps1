<#
.SYNOPSIS
Builds and deploys the configured WAP package.

.DESCRIPTION
Builds the packaging project with Visual Studio MSBuild (the WAP itself
restores the redirected publish graph before building the application),
unpacks the generated unsigned MSIX, and registers the unpacked
development package in Visual Studio's standard AppX output directory while
preserving application data. Use -Aot to enable Native AOT and trimming;
otherwise the script publishes a managed, untrimmed development package.
Repository-specific paths and platform mappings are loaded from
Package.config.psd1.

.EXAMPLE
.\eng\Deploy-Package.ps1

.EXAMPLE
.\eng\Deploy-Package.ps1 -Aot

.EXAMPLE
.\eng\Deploy-Package.ps1 -Configuration Debug -Platform ARM64 -Aot
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string] $Configuration,

    [Parameter()]
    [string] $Platform,

    [Parameter()]
    [switch] $Aot,

    [Parameter()]
    [string] $VisualStudioPath,

    [Parameter()]
    [string] $ConfigPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $PSScriptRoot 'Package.config.psd1'
}

$ConfigPath = [IO.Path]::GetFullPath($ConfigPath)
if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    throw "WAP package configuration was not found: '$ConfigPath'."
}

$packageConfig = Import-PowerShellDataFile -LiteralPath $ConfigPath
$requiredConfigKeys = @(
    'RepositoryRoot'
    'AppProjectPath'
    'WapProjectPath'
    'PackageManifestPath'
    'ArtifactsPath'
    'PackageExecutablePath'
    'DefaultConfiguration'
    'DefaultPlatform'
    'RuntimeIdentifiers'
)
foreach ($requiredConfigKey in $requiredConfigKeys) {
    if (-not $packageConfig.ContainsKey($requiredConfigKey)) {
        throw "WAP package configuration '$ConfigPath' is missing '$requiredConfigKey'."
    }
}

if ([string]::IsNullOrWhiteSpace($Configuration)) {
    $Configuration = [string] $packageConfig.DefaultConfiguration
}
if ([string]::IsNullOrWhiteSpace($Platform)) {
    $Platform = [string] $packageConfig.DefaultPlatform
}
if (-not ($packageConfig.RuntimeIdentifiers -is [Collections.IDictionary])) {
    throw "RuntimeIdentifiers in '$ConfigPath' must be a hashtable."
}
if (-not $packageConfig.RuntimeIdentifiers.Contains($Platform)) {
    $supportedPlatforms = @($packageConfig.RuntimeIdentifiers.Keys) -join ', '
    throw "Platform '$Platform' is not configured. Supported platforms: $supportedPlatforms."
}

$configDirectory = Split-Path -Parent $ConfigPath
$repoRoot = [IO.Path]::GetFullPath((Join-Path $configDirectory ([string] $packageConfig.RepositoryRoot)))

function Resolve-ConfiguredPath {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }

    return [IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

$appProjectPath = Resolve-ConfiguredPath ([string] $packageConfig.AppProjectPath)
$wapProjectPath = Resolve-ConfiguredPath ([string] $packageConfig.WapProjectPath)
$packageManifestPath = Resolve-ConfiguredPath ([string] $packageConfig.PackageManifestPath)
$artifactsPath = Resolve-ConfiguredPath ([string] $packageConfig.ArtifactsPath)
$packageExecutableRelativePath = [string] $packageConfig.PackageExecutablePath
if ([IO.Path]::IsPathRooted($packageExecutableRelativePath)) {
    throw "PackageExecutablePath in '$ConfigPath' must be relative to the package root."
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,

        [Parameter(Mandatory)]
        [string[]] $ArgumentList,

        [Parameter()]
        [switch] $SuppressOutput
    )

    if ($SuppressOutput) {
        & $FilePath @ArgumentList | Out-Null
    }
    else {
        & $FilePath @ArgumentList
    }

    if ($LASTEXITCODE -ne 0) {
        throw "'$FilePath' failed with exit code $LASTEXITCODE."
    }
}

function Find-VisualStudioInstallation {
    if ($VisualStudioPath) {
        $msbuildCandidate = Join-Path $VisualStudioPath 'MSBuild\Current\Bin\MSBuild.exe'
        $desktopBridgeCandidate = Join-Path $VisualStudioPath 'MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets'
        if ((Test-Path -LiteralPath $msbuildCandidate -PathType Leaf) -and
            (Test-Path -LiteralPath $desktopBridgeCandidate -PathType Leaf)) {
            return $VisualStudioPath
        }

        throw "Visual Studio with DesktopBridge packaging support was not found at '$VisualStudioPath'."
    }

    $vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswherePath -PathType Leaf)) {
        throw "vswhere.exe was not found at '$vswherePath'."
    }

    $installationPaths = @(
        & $vswherePath -latest -products * -prerelease -requires Microsoft.Component.MSBuild -property installationPath
    )
    if ($LASTEXITCODE -ne 0) {
        throw "vswhere.exe failed with exit code $LASTEXITCODE."
    }

    foreach ($installationPath in $installationPaths) {
        $msbuildPath = Join-Path $installationPath 'MSBuild\Current\Bin\MSBuild.exe'
        $desktopBridgeTargets = Join-Path $installationPath 'MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets'
        if ((Test-Path -LiteralPath $msbuildPath -PathType Leaf) -and
            (Test-Path -LiteralPath $desktopBridgeTargets -PathType Leaf)) {
            return $installationPath
        }
    }

    throw 'No Visual Studio installation with DesktopBridge packaging support was found.'
}

function Find-MakeAppx {
    $windowsKitsBinPath = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (-not (Test-Path -LiteralPath $windowsKitsBinPath -PathType Container)) {
        throw "Windows SDK tools were not found at '$windowsKitsBinPath'."
    }

    $candidate = Get-ChildItem -LiteralPath $windowsKitsBinPath -Recurse -File -Filter 'makeappx.exe' |
        Where-Object { $_.FullName -match '\\x64\\makeappx\.exe$' } |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending |
        Select-Object -First 1

    if (-not $candidate) {
        throw 'No x64 makeappx.exe was found in the installed Windows SDKs.'
    }

    return $candidate.FullName
}

function Remove-GeneratedDirectory {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $Root
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return
    }

    $resolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $resolvedPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not $resolvedPath.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove generated directory outside '$resolvedRoot': '$resolvedPath'."
    }

    $protectedAttributes = [IO.FileAttributes]::ReadOnly -bor
        [IO.FileAttributes]::Hidden -bor
        [IO.FileAttributes]::System
    foreach ($item in Get-ChildItem -LiteralPath $resolvedPath -Force -Recurse) {
        if (($item.Attributes -band $protectedAttributes) -ne 0) {
            $item.Attributes = $item.Attributes -band (-bnot $protectedAttributes)
        }
    }

    $rootItem = Get-Item -LiteralPath $resolvedPath -Force
    if (($rootItem.Attributes -band $protectedAttributes) -ne 0) {
        $rootItem.Attributes = $rootItem.Attributes -band (-bnot $protectedAttributes)
    }

    Remove-Item -LiteralPath $resolvedPath -Recurse -Force
}

foreach ($requiredPath in @($appProjectPath, $wapProjectPath, $packageManifestPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required repository file was not found: '$requiredPath'."
    }
}

[xml] $sourcePackageManifest = Get-Content -LiteralPath $packageManifestPath -Raw
$configuredPackageIdentityName = [string] $sourcePackageManifest.Package.Identity.Name
if ([string]::IsNullOrWhiteSpace($configuredPackageIdentityName)) {
    throw "Package identity name could not be read from '$packageManifestPath'."
}

$visualStudioInstallation = Find-VisualStudioInstallation
$msbuildPath = Join-Path $visualStudioInstallation 'MSBuild\Current\Bin\MSBuild.exe'
$makeAppxPath = Find-MakeAppx
$wapOutputPath = Join-Path (Split-Path -Parent $wapProjectPath) "bin\$Platform\$Configuration"
$publishAot = $Aot.IsPresent.ToString().ToLowerInvariant()
$publishTrimmed = $Aot.IsPresent.ToString().ToLowerInvariant()
$publishMode = if ($Aot) {
    'Native AOT with trimming'
}
else {
    'managed without trimming'
}

Write-Host "Building the WAP ($Configuration|$Platform, $publishMode)..."
Invoke-CheckedCommand -FilePath $msbuildPath -ArgumentList @(
    $wapProjectPath
    '/restore'
    '/t:Build'
    "/p:Configuration=$Configuration"
    "/p:Platform=$Platform"
    '/p:GenerateAppxPackageOnBuild=true'
    "/p:PublishAot=$publishAot"
    "/p:PublishTrimmed=$publishTrimmed"
    '/p:AppxBundle=Never'
    '/p:AppxPackageSigningEnabled=false'
    '/nologo'
    '/m'
    '/v:minimal'
)

$architectureSuffix = "_$Platform"
$msixPackage = Get-ChildItem -LiteralPath $artifactsPath -Recurse -File -Filter '*.msix' |
    Where-Object { $_.BaseName.EndsWith($architectureSuffix, [StringComparison]::OrdinalIgnoreCase) } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $msixPackage) {
    throw "The WAP build did not produce a $Platform MSIX under '$artifactsPath'."
}

$deploymentPath = Join-Path $wapOutputPath 'AppX'
$stagingPath = Join-Path $wapOutputPath "AppX.staging-$PID"
$backupPath = Join-Path $wapOutputPath "AppX.backup-$PID"

foreach ($temporaryPath in @($stagingPath, $backupPath)) {
    if (Test-Path -LiteralPath $temporaryPath) {
        throw "Temporary deployment path already exists: '$temporaryPath'."
    }
}

Write-Host "Unpacking '$($msixPackage.FullName)'..."
Invoke-CheckedCommand -FilePath $makeAppxPath -ArgumentList @(
    'unpack'
    '/p'
    $msixPackage.FullName
    '/d'
    $stagingPath
    '/o'
) -SuppressOutput

$stagingManifestPath = Join-Path $stagingPath 'AppxManifest.xml'
$stagingExecutablePath = Join-Path $stagingPath $packageExecutableRelativePath
$stagingCoreClrPath = Join-Path (Split-Path -Parent $stagingExecutablePath) 'coreclr.dll'

if (-not (Test-Path -LiteralPath $stagingManifestPath -PathType Leaf)) {
    throw "The unpacked package has no manifest at '$stagingManifestPath'."
}
if (-not (Test-Path -LiteralPath $stagingExecutablePath -PathType Leaf)) {
    throw "The unpacked package has no configured executable at '$stagingExecutablePath'."
}
if ($Aot -and (Test-Path -LiteralPath $stagingCoreClrPath -PathType Leaf)) {
    throw "The generated package contains coreclr.dll and is not the expected Native AOT deployment."
}
if ((-not $Aot) -and (-not (Test-Path -LiteralPath $stagingCoreClrPath -PathType Leaf))) {
    throw 'The generated package has no coreclr.dll and is not the expected managed deployment.'
}

[xml] $deploymentManifest = Get-Content -LiteralPath $stagingManifestPath -Raw
$packageIdentityName = [string] $deploymentManifest.Package.Identity.Name
if (-not $packageIdentityName.Equals($configuredPackageIdentityName, [StringComparison]::Ordinal)) {
    throw "Built package identity '$packageIdentityName' does not match configured identity '$configuredPackageIdentityName'."
}
$previousPackage = Get-AppxPackage -Name $packageIdentityName | Select-Object -First 1
$previousInstallLocation = if ($previousPackage) {
    [string] $previousPackage.InstallLocation
}

if ($previousPackage) {
    Write-Host "Removing the existing development registration while preserving app data..."
    Remove-AppxPackage -Package $previousPackage.PackageFullName -PreserveApplicationData -ErrorAction Stop
}

$layoutBackupCreated = $false
try {
    if (Test-Path -LiteralPath $deploymentPath -PathType Container) {
        [IO.Directory]::Move($deploymentPath, $backupPath)
        $layoutBackupCreated = $true
    }

    [IO.Directory]::Move($stagingPath, $deploymentPath)
    $deploymentManifestPath = Join-Path $deploymentPath 'AppxManifest.xml'

    Write-Host "Registering the unpacked package from Visual Studio's AppX output directory..."
    Add-AppxPackage -Register $deploymentManifestPath -ForceApplicationShutdown -ErrorAction Stop
}
catch {
    Remove-GeneratedDirectory -Path $deploymentPath -Root $wapOutputPath
    if (Test-Path -LiteralPath $backupPath -PathType Container) {
        [IO.Directory]::Move($backupPath, $deploymentPath)
    }

    $previousManifestPath = if ($previousInstallLocation) {
        Join-Path $previousInstallLocation 'AppxManifest.xml'
    }

    if ($previousManifestPath -and (Test-Path -LiteralPath $previousManifestPath -PathType Leaf)) {
        Write-Warning 'Deployment failed; restoring the previous development registration.'
        Add-AppxPackage -Register $previousManifestPath -ForceApplicationShutdown -ErrorAction Stop
    }

    throw
}

$deployedPackage = Get-AppxPackage -Name $packageIdentityName | Select-Object -First 1
if (-not $deployedPackage) {
    throw "Package '$packageIdentityName' was not registered."
}

$actualInstallLocation = [IO.Path]::GetFullPath([string] $deployedPackage.InstallLocation).TrimEnd('\')
$expectedInstallLocation = [IO.Path]::GetFullPath($deploymentPath).TrimEnd('\')
if (-not $actualInstallLocation.Equals($expectedInstallLocation, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Package '$packageIdentityName' was registered from '$actualInstallLocation', not '$expectedInstallLocation'."
}

if ($layoutBackupCreated) {
    Write-Host "Removing the previous AppX layout backup..."
    Remove-GeneratedDirectory -Path $backupPath -Root $wapOutputPath
}

foreach ($pattern in @('AotAppX-*', 'AppX.staging-*', 'AppX.backup-*')) {
    foreach ($obsoleteDeploymentPath in Get-ChildItem -LiteralPath $wapOutputPath -Directory -Filter $pattern) {
        Write-Host "Removing obsolete generated deployment layout '$($obsoleteDeploymentPath.FullName)'..."
        Remove-GeneratedDirectory -Path $obsoleteDeploymentPath.FullName -Root $wapOutputPath
    }
}

Write-Host "WAP build and deployment completed successfully ($publishMode):"
Write-Host "  $actualInstallLocation"
