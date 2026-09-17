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

Requires Visual Studio 2026 MSBuild with -getResultOutputFile support.
Automatic discovery uses released installations by default. Use
-IncludePrerelease to also consider preview or Insiders installations, or
-VisualStudioPath to select an installation explicitly.
Successful deployments print the installed version, build mode, timestamps,
package age from the MSIX last-write time, elapsed time, and output paths.

.EXAMPLE
.\eng\Deploy-Package.ps1

.EXAMPLE
.\eng\Deploy-Package.ps1 -Aot

.EXAMPLE
.\eng\Deploy-Package.ps1 -Configuration Debug -Platform ARM64 -Aot

.EXAMPLE
.\eng\Deploy-Package.ps1 -IncludePrerelease
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
    [switch] $IncludePrerelease,

    [Parameter()]
    [string] $ConfigPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$deploymentTimer = [Diagnostics.Stopwatch]::StartNew()

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $PSScriptRoot 'Package.config.psd1'
}

$ConfigPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ConfigPath)
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
    if (-not $packageConfig.ContainsKey($requiredConfigKey) -or
        [string]::IsNullOrWhiteSpace([string] $packageConfig[$requiredConfigKey])) {
        throw "WAP package configuration '$ConfigPath' is missing a value for '$requiredConfigKey'."
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
if ([IO.Path]::IsPathRooted($packageExecutableRelativePath) -or
    '..' -in ($packageExecutableRelativePath -split '[\\/]')) {
    throw "PackageExecutablePath in '$ConfigPath' must stay within the package root."
}

function Resolve-GeneratedPath {
    param(
        [Parameter(Mandatory)]
        [string] $Root,

        [Parameter(Mandatory)]
        [string] $Path
    )

    $resolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + '\'
    $resolvedPath = [IO.Path]::GetFullPath((Join-Path $resolvedRoot $Path)).TrimEnd('\', '/')
    if (-not $resolvedPath.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Generated path must be inside '$resolvedRoot': '$resolvedPath'."
    }

    return $resolvedPath
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
        $commandOutput = & $FilePath @ArgumentList
    }
    else {
        & $FilePath @ArgumentList
    }

    if ($LASTEXITCODE -ne 0) {
        if ($SuppressOutput) {
            $commandOutput | Out-Host
        }
        throw "'$FilePath' failed with exit code $LASTEXITCODE."
    }
}

function Find-VisualStudioInstallation {
    if ($VisualStudioPath) {
        $VisualStudioPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($VisualStudioPath)
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

    $vswhereArguments = @(
        '-sort'
        '-version', '[18.0,)'
        '-products', '*'
        '-requires', 'Microsoft.Component.MSBuild'
        '-property', 'installationPath'
    )
    if ($IncludePrerelease) {
        $vswhereArguments += '-prerelease'
    }

    $installationPaths = @(& $vswherePath @vswhereArguments)
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

    $prereleaseHint = if (-not $IncludePrerelease) {
        ' Use -IncludePrerelease to also consider preview or Insiders installations.'
    }
    else {
        ''
    }
    throw "No eligible Visual Studio 2026 installation with DesktopBridge packaging support was found.$prereleaseHint"
}

function Find-MakeAppx {
    $windowsKitsBinPath = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (-not (Test-Path -LiteralPath $windowsKitsBinPath -PathType Container)) {
        throw "Windows SDK tools were not found at '$windowsKitsBinPath'."
    }

    $candidate = Get-ChildItem -LiteralPath $windowsKitsBinPath -Directory |
        Where-Object {
            $sdkVersion = $null
            [version]::TryParse($_.Name, [ref] $sdkVersion)
        } |
        Sort-Object { [version] $_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\makeappx.exe' } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1

    if (-not $candidate) {
        throw 'No x64 makeappx.exe was found in the installed Windows SDKs.'
    }

    return $candidate
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

function Format-ElapsedTime {
    param(
        [Parameter(Mandatory)]
        [TimeSpan] $Duration
    )

    if ($Duration.TotalDays -ge 1) {
        return '{0}d {1}h {2}m {3}s' -f $Duration.Days, $Duration.Hours, $Duration.Minutes, $Duration.Seconds
    }
    if ($Duration.TotalHours -ge 1) {
        return '{0}h {1}m {2}s' -f $Duration.Hours, $Duration.Minutes, $Duration.Seconds
    }
    if ($Duration.TotalMinutes -ge 1) {
        return '{0}m {1}s' -f $Duration.Minutes, $Duration.Seconds
    }

    return '{0}s' -f $Duration.Seconds
}

foreach ($requiredPath in @($appProjectPath, $wapProjectPath, $packageManifestPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required repository file was not found: '$requiredPath'."
    }
}

[xml] $sourcePackageManifest = Get-Content -LiteralPath $packageManifestPath -Raw
$configuredPackageIdentityName = [string] $sourcePackageManifest.Package.Identity.Name
$configuredPackagePublisher = [string] $sourcePackageManifest.Package.Identity.Publisher
if ([string]::IsNullOrWhiteSpace($configuredPackageIdentityName)) {
    throw "Package identity name could not be read from '$packageManifestPath'."
}
if ([string]::IsNullOrWhiteSpace($configuredPackagePublisher)) {
    throw "Package publisher could not be read from '$packageManifestPath'."
}

$visualStudioInstallation = Find-VisualStudioInstallation
$msbuildPath = Join-Path $visualStudioInstallation 'MSBuild\Current\Bin\MSBuild.exe'
$makeAppxPath = Find-MakeAppx
$wapOutputPath = Resolve-GeneratedPath -Root (Join-Path (Split-Path -Parent $wapProjectPath) 'bin') -Path "$Platform\$Configuration"
$publishAot = $Aot.IsPresent.ToString().ToLowerInvariant()
$publishTrimmed = $Aot.IsPresent.ToString().ToLowerInvariant()
$publishMode = if ($Aot) {
    'Native AOT with trimming'
}
else {
    'managed without trimming'
}

$deploymentId = "$PID-$([Guid]::NewGuid().ToString('N'))"
$deploymentPath = Resolve-GeneratedPath -Root $wapOutputPath -Path 'AppX'
$stagingPath = Resolve-GeneratedPath -Root $wapOutputPath -Path "AppX.staging-$deploymentId"
$backupPath = Resolve-GeneratedPath -Root $wapOutputPath -Path "AppX.backup-$deploymentId"
$buildResultPath = Resolve-GeneratedPath -Root $wapOutputPath -Path "AppX.build-$deploymentId.txt"
[IO.Directory]::CreateDirectory($wapOutputPath) | Out-Null

try {
    Write-Host "Building the WAP ($Configuration|$Platform, $publishMode)..."
    Invoke-CheckedCommand -FilePath $msbuildPath -ArgumentList @(
        $wapProjectPath
        '/restore'
        '/t:Build'
        "/p:Configuration=$Configuration"
        "/p:Platform=$Platform"
        '/p:GenerateAppxPackageOnBuild=true'
        '/p:BuildingProject=true'
        "/p:PublishAot=$publishAot"
        "/p:PublishTrimmed=$publishTrimmed"
        '/p:AppxBundle=Never'
        '/p:AppxPackageSigningEnabled=false'
        "/p:AppxPackageDir=$($artifactsPath.TrimEnd('\', '/'))/"
        '/getProperty:AppxPackageOutput'
        "/getResultOutputFile:$buildResultPath"
        '/nologo'
        '/m'
        '/v:minimal'
    )

    if (-not (Test-Path -LiteralPath $buildResultPath -PathType Leaf)) {
        throw 'MSBuild did not report the generated package path.'
    }
    $msixPackagePath = Get-Content -LiteralPath $buildResultPath -Raw
    if ([string]::IsNullOrWhiteSpace($msixPackagePath)) {
        throw 'The WAP build did not report AppxPackageOutput.'
    }
    $msixPackagePath = $msixPackagePath.Trim()
    if (-not [IO.Path]::IsPathRooted($msixPackagePath)) {
        $msixPackagePath = Join-Path (Split-Path -Parent $wapProjectPath) $msixPackagePath
    }
    if ([IO.Path]::GetExtension($msixPackagePath) -ne '.msix' -or
        -not (Test-Path -LiteralPath $msixPackagePath -PathType Leaf)) {
        throw "The WAP build did not produce the reported MSIX: '$msixPackagePath'."
    }
    $packageFile = Get-Item -LiteralPath $msixPackagePath
    $msixPackagePath = $packageFile.FullName
    $packageTimestampUtc = $packageFile.LastWriteTimeUtc

    Write-Host "Unpacking '$msixPackagePath'..."
    Invoke-CheckedCommand -FilePath $makeAppxPath -ArgumentList @(
        'unpack'
        '/p'
        $msixPackagePath
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
    $appProductVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($stagingExecutablePath).ProductVersion

    [xml] $deploymentManifest = Get-Content -LiteralPath $stagingManifestPath -Raw
    $packageIdentityName = [string] $deploymentManifest.Package.Identity.Name
    if (-not $packageIdentityName.Equals($configuredPackageIdentityName, [StringComparison]::Ordinal)) {
        throw "Built package identity '$packageIdentityName' does not match configured identity '$configuredPackageIdentityName'."
    }
    $packagePublisher = [string] $deploymentManifest.Package.Identity.Publisher
    if (-not $packagePublisher.Equals($configuredPackagePublisher, [StringComparison]::Ordinal)) {
        throw "Built package publisher '$packagePublisher' does not match configured publisher '$configuredPackagePublisher'."
    }
    $packageArchitecture = [string] $deploymentManifest.Package.Identity.ProcessorArchitecture
    if (-not $packageArchitecture.Equals($Platform, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Built package architecture '$packageArchitecture' does not match requested platform '$Platform'."
    }
    $packageVersion = [version] $deploymentManifest.Package.Identity.Version
    $previousPackages = @(Get-AppxPackage -Name $packageIdentityName -Publisher $packagePublisher -ErrorAction Stop)
    if ($previousPackages.Count -gt 1) {
        throw "More than one registration matches '$packageIdentityName' and '$packagePublisher'."
    }
    $previousPackage = $previousPackages | Select-Object -First 1
    $previousManifestPath = $null
    if ($previousPackage) {
        if (-not $previousPackage.IsDevelopmentMode) {
            throw "Package '$packageIdentityName' is not a development registration; its application data cannot be preserved by this script."
        }
        $previousManifestPath = Join-Path ([string] $previousPackage.InstallLocation) 'AppxManifest.xml'
        if (-not (Test-Path -LiteralPath $previousManifestPath -PathType Leaf)) {
            throw "The previous package manifest is unavailable for rollback: '$previousManifestPath'."
        }
    }

    $previousRegistrationRemoved = $false
    $layoutBackupCreated = $false
    $newLayoutInstalled = $false
    try {
        if ($previousPackage) {
            Write-Host 'Removing the existing development registration while preserving app data...'
            Remove-AppxPackage -Package $previousPackage.PackageFullName -PreserveApplicationData -ErrorAction Stop
            $previousRegistrationRemoved = $true
        }

        if (Test-Path -LiteralPath $deploymentPath -PathType Container) {
            [IO.Directory]::Move($deploymentPath, $backupPath)
            $layoutBackupCreated = $true
        }

        [IO.Directory]::Move($stagingPath, $deploymentPath)
        $newLayoutInstalled = $true
        $deploymentManifestPath = Join-Path $deploymentPath 'AppxManifest.xml'

        Write-Host "Registering the unpacked package from Visual Studio's AppX output directory..."
        Add-AppxPackage -Register $deploymentManifestPath -ForceApplicationShutdown -ErrorAction Stop

        $deployedPackages = @(Get-AppxPackage -Name $packageIdentityName -Publisher $packagePublisher -ErrorAction Stop)
        if ($deployedPackages.Count -ne 1) {
            throw "Package '$packageIdentityName' does not have exactly one registration."
        }
        $deployedPackage = $deployedPackages[0]
        $actualInstallLocation = [IO.Path]::GetFullPath([string] $deployedPackage.InstallLocation).TrimEnd('\')
        if (-not $actualInstallLocation.Equals($deploymentPath, [StringComparison]::OrdinalIgnoreCase) -or
            [version] $deployedPackage.Version -ne $packageVersion -or
            [string] $deployedPackage.Architecture -ne $packageArchitecture -or
            -not $deployedPackage.IsDevelopmentMode) {
            throw "Package '$packageIdentityName' was not registered with the expected layout, version, architecture and development mode."
        }
        $deployedAtUtc = [DateTime]::UtcNow
    }
    catch {
        $deploymentError = $_
        try {
            if ($newLayoutInstalled) {
                $failedPackages = @(Get-AppxPackage -Name $packageIdentityName -Publisher $packagePublisher -ErrorAction Stop)
                foreach ($failedPackage in $failedPackages) {
                    $failedInstallLocation = [IO.Path]::GetFullPath([string] $failedPackage.InstallLocation).TrimEnd('\')
                    if ($failedInstallLocation.Equals($deploymentPath, [StringComparison]::OrdinalIgnoreCase)) {
                        Remove-AppxPackage -Package $failedPackage.PackageFullName -PreserveApplicationData -ErrorAction Stop
                    }
                }
                Remove-GeneratedDirectory -Path $deploymentPath -Root $wapOutputPath
            }
            if ($layoutBackupCreated) {
                [IO.Directory]::Move($backupPath, $deploymentPath)
            }
            if ($previousRegistrationRemoved) {
                Write-Warning 'Deployment failed; restoring the previous development registration.' -WarningAction Continue
                Add-AppxPackage -Register $previousManifestPath -ForceApplicationShutdown -ErrorAction Stop
            }
        }
        catch {
            Write-Warning "Deployment recovery failed: $_. Any remaining layout backup is at '$backupPath'." -WarningAction Continue
        }

        throw $deploymentError
    }

    if ($layoutBackupCreated) {
        try {
            Write-Host 'Removing the previous AppX layout backup...'
            Remove-GeneratedDirectory -Path $backupPath -Root $wapOutputPath
        }
        catch {
            Write-Warning "Deployment succeeded, but the previous layout could not be removed: $_" -WarningAction Continue
        }
    }
}
finally {
    try {
        Remove-GeneratedDirectory -Path $stagingPath -Root $wapOutputPath
    }
    catch {
        Write-Warning "Could not remove staging layout '$stagingPath': $_" -WarningAction Continue
    }
    if (Test-Path -LiteralPath $buildResultPath -PathType Leaf) {
        Remove-Item -LiteralPath $buildResultPath -Force -ErrorAction Continue
    }
}

$deploymentTimer.Stop()
$packageAge = $deployedAtUtc - $packageTimestampUtc
$packageAgeText = if ($packageAge -ge [TimeSpan]::Zero) {
    "$(Format-ElapsedTime -Duration $packageAge) ago"
}
else {
    "$(Format-ElapsedTime -Duration $packageAge.Duration()) ahead of deployment time"
}
$timestampFormat = 'yyyy-MM-dd HH:mm:ss zzz'
$timestampCulture = [Globalization.CultureInfo]::InvariantCulture

Write-Host ''
Write-Host 'Deployment completed successfully:'
Write-Host "  Package:       $($deployedPackage.PackageFullName)"
Write-Host "  Version:       $($deployedPackage.Version)"
if (-not [string]::IsNullOrWhiteSpace($appProductVersion)) {
    Write-Host "  App build:     $appProductVersion"
}
Write-Host "  Configuration: $Configuration | $($deployedPackage.Architecture) | $publishMode"
Write-Host "  Package time:  $($packageTimestampUtc.ToLocalTime().ToString($timestampFormat, $timestampCulture)) (MSIX last write)"
Write-Host "  Build age:     $packageAgeText (at deployment)"
Write-Host "  Deployed at:   $($deployedAtUtc.ToLocalTime().ToString($timestampFormat, $timestampCulture))"
Write-Host "  Elapsed:       $(Format-ElapsedTime -Duration $deploymentTimer.Elapsed) (build and deployment)"
Write-Host "  Source MSIX:   $msixPackagePath"
Write-Host "  Installed at:  $actualInstallLocation"
