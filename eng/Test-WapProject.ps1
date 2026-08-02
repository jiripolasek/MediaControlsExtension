<#
.SYNOPSIS
Checks Command Palette WAP packaging conventions without building the project.

.DESCRIPTION
Validates the shared property contract, WAP wiring, package manifest, provider
CLSID, executable path, package assets, publish profiles, and solution deploy
mapping. Failures produce exit code 1. Warnings require review but do not fail
the process.

.EXAMPLE
.\eng\Test-WapProject.ps1

.EXAMPLE
.\eng\Test-WapProject.ps1 -OutputFormat Json
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string] $RepositoryRoot,

    [Parameter()]
    [string] $WapProjectPath,

    [Parameter()]
    [string] $ExtensionPropsPath,

    [Parameter()]
    [ValidateSet('Text', 'Json')]
    [string] $OutputFormat = 'Text'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$results = [Collections.Generic.List[object]]::new()

function Add-Result {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Pass', 'Warning', 'Fail')]
        [string] $Status,

        [Parameter(Mandatory)]
        [string] $Check,

        [Parameter(Mandatory)]
        [string] $Message
    )

    $results.Add([pscustomobject]@{
        Status  = $Status
        Check   = $Check
        Message = $Message
    })
}

function Get-XmlProperty {
    param(
        [Parameter(Mandatory)]
        [xml] $Document,

        [Parameter(Mandatory)]
        [string] $Name
    )

    $node = $Document.SelectSingleNode("//*[local-name()='$Name']")
    if ($null -eq $node) {
        return $null
    }

    return [string] $node.InnerText.Trim()
}

function Resolve-RepositoryPropertyPath {
    param(
        [Parameter(Mandatory)]
        [string] $Value,

        [Parameter(Mandatory)]
        [string] $Root
    )

    $rootWithSeparator = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $expanded = $Value.Replace('$(CmdPalRepositoryRoot)', $rootWithSeparator)
    if ($expanded.Contains('$(')) {
        return $null
    }

    if ([IO.Path]::IsPathRooted($expanded)) {
        return [IO.Path]::GetFullPath($expanded)
    }

    return [IO.Path]::GetFullPath((Join-Path $Root $expanded))
}

function Test-PackageAsset {
    param(
        [Parameter(Mandatory)]
        [string] $AssetPath,

        [Parameter(Mandatory)]
        [string] $PackageProjectDirectory
    )

    $candidate = Join-Path $PackageProjectDirectory $AssetPath
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        return $true
    }

    $candidateDirectory = Split-Path -Parent $candidate
    if (-not (Test-Path -LiteralPath $candidateDirectory -PathType Container)) {
        return $false
    }

    $fileName = [IO.Path]::GetFileNameWithoutExtension($candidate)
    $extension = [IO.Path]::GetExtension($candidate)
    $qualifiedAssets = @(
        Get-ChildItem -LiteralPath $candidateDirectory -File |
            Where-Object {
                $_.Name.StartsWith("$fileName.", [StringComparison]::OrdinalIgnoreCase) -and
                $_.Extension.Equals($extension, [StringComparison]::OrdinalIgnoreCase)
            }
    )

    return $qualifiedAssets.Count -gt 0
}

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) {
    throw "Repository root was not found: '$RepositoryRoot'."
}
$directoryBuildPropsPath = Join-Path $RepositoryRoot 'Directory.Build.props'
$directoryBuildPropsText = ''
$directoryBuildProps = $null
if (Test-Path -LiteralPath $directoryBuildPropsPath -PathType Leaf) {
    $directoryBuildPropsText = Get-Content -LiteralPath $directoryBuildPropsPath -Raw
    [xml] $directoryBuildProps = $directoryBuildPropsText
}

if ([string]::IsNullOrWhiteSpace($WapProjectPath)) {
    $wapProjects = @(
        Get-ChildItem -LiteralPath $RepositoryRoot -Recurse -File -Filter '*.wapproj' |
            Where-Object {
                [IO.Path]::GetRelativePath(
                    $RepositoryRoot,
                    $_.FullName) -notmatch '(^|[\\/])(bin|obj)[\\/]'
            }
    )
    if ($wapProjects.Count -ne 1) {
        Add-Result `
            -Status Fail `
            -Check 'WAP discovery' `
            -Message "Expected exactly one .wapproj, found $($wapProjects.Count). Pass -WapProjectPath."
    }
    else {
        $WapProjectPath = $wapProjects[0].FullName
        Add-Result `
            -Status Pass `
            -Check 'WAP discovery' `
            -Message "Found '$WapProjectPath'."
    }
}
elseif (-not [IO.Path]::IsPathRooted($WapProjectPath)) {
    $WapProjectPath = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot $WapProjectPath))
}

if ([string]::IsNullOrWhiteSpace($ExtensionPropsPath)) {
    $ExtensionPropsPath = Join-Path $RepositoryRoot 'eng\CmdPal.Extension.props'
}
elseif (-not [IO.Path]::IsPathRooted($ExtensionPropsPath)) {
    $ExtensionPropsPath = [IO.Path]::GetFullPath(
        (Join-Path $RepositoryRoot $ExtensionPropsPath))
}

if ([string]::IsNullOrWhiteSpace($WapProjectPath) -or
    -not (Test-Path -LiteralPath $WapProjectPath -PathType Leaf)) {
    Add-Result `
        -Status Fail `
        -Check 'WAP project' `
        -Message "WAP project was not found: '$WapProjectPath'."
}

if (-not (Test-Path -LiteralPath $ExtensionPropsPath -PathType Leaf)) {
    Add-Result `
        -Status Fail `
        -Check 'Shared properties' `
        -Message "Shared property contract was not found: '$ExtensionPropsPath'."
}

$canContinue = @($results | Where-Object Status -eq 'Fail').Count -eq 0
if ($canContinue) {
    [xml] $wapProject = Get-Content -LiteralPath $WapProjectPath -Raw
    [xml] $extensionProps = Get-Content -LiteralPath $ExtensionPropsPath -Raw
    $wapText = Get-Content -LiteralPath $WapProjectPath -Raw
    $extensionPropsText = Get-Content -LiteralPath $ExtensionPropsPath -Raw
    $packageProjectDirectory = Split-Path -Parent $WapProjectPath

    foreach ($requiredProperty in @(
            'CmdPalExtensionProject',
            'CmdPalExtensionProjectReference',
            'CmdPalExtensionAssemblyName',
            'CmdPalPackageProject',
            'CmdPalPackageManifest',
            'CmdPalPackageExecutable',
            'CmdPalPackageProjectGuid',
            'CmdPalTargetFramework',
            'CmdPalTargetPlatformVersion',
            'CmdPalTargetPlatformMinVersion',
            'CmdPalRuntimeIdentifiers',
            'CmdPalArtifactsPath',
            'CmdPalSupportsNativeAot')) {
        $propertyValue = Get-XmlProperty -Document $extensionProps -Name $requiredProperty
        if ([string]::IsNullOrWhiteSpace($propertyValue)) {
            Add-Result `
                -Status Fail `
                -Check 'Shared properties' `
                -Message "Required property '$requiredProperty' is missing or empty."
        }
        else {
            Add-Result `
                -Status Pass `
                -Check 'Shared properties' `
                -Message "'$requiredProperty' is declared."
        }
    }

    if ($wapText -match '[A-Za-z]:\\' -or $extensionPropsText -match '[A-Za-z]:\\') {
        Add-Result `
            -Status Fail `
            -Check 'Portable paths' `
            -Message 'The WAP or shared property contract contains a literal absolute Windows path.'
    }
    else {
        Add-Result `
            -Status Pass `
            -Check 'Portable paths' `
            -Message 'No literal absolute Windows paths were found.'
    }

    $appProjectValue = Get-XmlProperty `
        -Document $extensionProps `
        -Name 'CmdPalExtensionProject'
    $appProjectPath = Resolve-RepositoryPropertyPath `
        -Value $appProjectValue `
        -Root $RepositoryRoot
    if ($null -eq $appProjectPath -or
        -not (Test-Path -LiteralPath $appProjectPath -PathType Leaf)) {
        Add-Result `
            -Status Fail `
            -Check 'Application project' `
            -Message "Configured application project could not be resolved: '$appProjectValue'."
    }
    else {
        Add-Result `
            -Status Pass `
            -Check 'Application project' `
            -Message "Resolved '$appProjectPath'."
    }

    $appProjectReferenceValue = Get-XmlProperty `
        -Document $extensionProps `
        -Name 'CmdPalExtensionProjectReference'
    $appProjectReferencePath = $null
    if (-not [string]::IsNullOrWhiteSpace($appProjectReferenceValue) -and
        -not [IO.Path]::IsPathRooted($appProjectReferenceValue) -and
        -not $appProjectReferenceValue.Contains('$(')) {
        $appProjectReferencePath = [IO.Path]::GetFullPath(
            (Join-Path $packageProjectDirectory $appProjectReferenceValue))
    }
    if ($null -eq $appProjectReferencePath -or
        $null -eq $appProjectPath -or
        -not $appProjectReferencePath.Equals(
            $appProjectPath,
            [StringComparison]::OrdinalIgnoreCase)) {
        Add-Result `
            -Status Fail `
            -Check 'Application project reference' `
            -Message "CmdPalExtensionProjectReference must be WAP-relative and resolve to '$appProjectPath'."
    }
    else {
        Add-Result `
            -Status Pass `
            -Check 'Application project reference' `
            -Message "WAP-relative reference resolves to '$appProjectReferencePath'."
    }

    $configuredWapValue = Get-XmlProperty `
        -Document $extensionProps `
        -Name 'CmdPalPackageProject'
    $configuredWapPath = Resolve-RepositoryPropertyPath `
        -Value $configuredWapValue `
        -Root $RepositoryRoot
    if ($null -eq $configuredWapPath -or
        -not $configuredWapPath.Equals(
            [IO.Path]::GetFullPath($WapProjectPath),
            [StringComparison]::OrdinalIgnoreCase)) {
        Add-Result `
            -Status Fail `
            -Check 'Package project contract' `
            -Message "CmdPalPackageProject does not resolve to '$WapProjectPath'."
    }
    else {
        Add-Result `
            -Status Pass `
            -Check 'Package project contract' `
            -Message 'CmdPalPackageProject resolves to the checked WAP.'
    }

    $manifestValue = Get-XmlProperty `
        -Document $extensionProps `
        -Name 'CmdPalPackageManifest'
    $manifestPath = Resolve-RepositoryPropertyPath `
        -Value $manifestValue `
        -Root $RepositoryRoot
    if ($null -eq $manifestPath -or
        -not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        Add-Result `
            -Status Fail `
            -Check 'Package manifest' `
            -Message "Configured package manifest could not be resolved: '$manifestValue'."
    }
    else {
        Add-Result `
            -Status Pass `
            -Check 'Package manifest' `
            -Message "Resolved '$manifestPath'."
    }

    $configurationNames = @(
        $wapProject.SelectNodes("//*[local-name()='ProjectConfiguration']") |
            ForEach-Object { $_.GetAttribute('Include') }
    )
    $requiredConfigurations = @(
        'Debug|x64',
        'Release|x64',
        'Debug|ARM64',
        'Release|ARM64'
    )
    $missingConfigurations = @(
        $requiredConfigurations |
            Where-Object { $_ -notin $configurationNames }
    )
    if ($missingConfigurations.Count -ne 0) {
        Add-Result `
            -Status Fail `
            -Check 'WAP configurations' `
            -Message "Missing configurations: $($missingConfigurations -join ', ')."
    }
    else {
        Add-Result `
            -Status Pass `
            -Check 'WAP configurations' `
            -Message 'Debug and Release are configured for x64 and ARM64.'
    }

    $entryPointProject = Get-XmlProperty `
        -Document $wapProject `
        -Name 'EntryPointProjectUniqueName'
    $projectReference = $wapProject.SelectSingleNode(
        "//*[local-name()='ProjectReference']")
    $projectReferenceInclude = if ($null -ne $projectReference) {
        [string] $projectReference.GetAttribute('Include')
    }
    if ($entryPointProject -ne '$(CmdPalExtensionProjectReference)' -or
        $projectReferenceInclude -ne '$(CmdPalExtensionProjectReference)') {
        Add-Result `
            -Status Fail `
            -Check 'WAP entry point' `
            -Message 'EntryPointProjectUniqueName and ProjectReference must use $(CmdPalExtensionProjectReference).'
    }
    else {
        Add-Result `
            -Status Pass `
            -Check 'WAP entry point' `
            -Message 'Entry point and ProjectReference use the shared contract.'
    }

    $publishRestoreTarget = $wapProject.SelectSingleNode(
        "//*[local-name()='Target' and @Name='RestoreCmdPalWapPublishAssets']")
    $publishRestoreTask = if ($null -ne $publishRestoreTarget) {
        $publishRestoreTarget.SelectSingleNode("./*[local-name()='MSBuild']")
    }
    $publishRestoreTargetIsValid =
        $null -ne $publishRestoreTarget -and
        $publishRestoreTarget.GetAttribute('BeforeTargets') -eq '_BuildDependentProjects' -and
        $publishRestoreTarget.GetAttribute('DependsOnTargets') -eq '_WapProjGetProjectClosure' -and
        $publishRestoreTarget.GetAttribute('Condition').Contains('_IsGeneratingAppxPackage') -and
        -not $publishRestoreTarget.GetAttribute('Condition').Contains('BuildingInsideVisualStudio') -and
        $null -ne $publishRestoreTask -and
        $publishRestoreTask.GetAttribute('Projects') -eq '@(ProjectReferenceWithPublishProps)' -and
        $publishRestoreTask.GetAttribute('Targets') -eq 'Restore' -and
        $publishRestoreTask.GetAttribute('Properties') -eq '%(ProjectReferenceWithPublishProps.SetConfiguration);%(ProjectReferenceWithPublishProps.SetPlatform)' -and
        $publishRestoreTask.GetAttribute('Condition').Contains('.NETCoreApp')
    if (-not $publishRestoreTargetIsValid) {
        Add-Result `
            -Status Fail `
            -Check 'Publish restore' `
            -Message 'The WAP must restore DesktopBridge ProjectReferenceWithPublishProps before _BuildDependentProjects for every packaging build, not only inside Visual Studio.'
    }
    else {
        Add-Result `
            -Status Pass `
            -Check 'Publish restore' `
            -Message 'The WAP restores the evaluated publish graph before building it.'
    }

    $packageAssetItem = $wapProject.SelectSingleNode(
        "//*[local-name()='Content' and starts-with(@Include, 'Assets\')]")
    $packagePriItem = $wapProject.SelectSingleNode(
        "//*[local-name()='PRIResource' and starts-with(@Include, 'Strings\')]")
    if ($null -eq $packageAssetItem -or
        $null -eq $packagePriItem -or
        $wapText.Contains('CmdPalExtensionProjectDirectory')) {
        Add-Result `
            -Status Fail `
            -Check 'Package resource ownership' `
            -Message 'Assets and PRI strings must be owned locally by the WAP project.'
    }
    else {
        Add-Result `
            -Status Pass `
            -Check 'Package resource ownership' `
            -Message 'Assets and PRI strings use WAP-local paths.'
    }

    $storeAssociation = $wapProject.SelectSingleNode(
        "//*[local-name()='None' and @Include='Package.StoreAssociation.xml']")
    if ($null -eq $storeAssociation -or
        -not $storeAssociation.GetAttribute('Condition').Contains('Exists')) {
        Add-Result `
            -Status Fail `
            -Check 'Store association' `
            -Message 'Package.StoreAssociation.xml must be optional and guarded by Exists(...).'
    }
    else {
        Add-Result `
            -Status Pass `
            -Check 'Store association' `
            -Message 'Store association is optional.'
    }

    if ($wapProject.SelectSingleNode("//*[local-name()='PackageCertificateThumbprint']")) {
        Add-Result `
            -Status Fail `
            -Check 'Signing' `
            -Message 'A certificate thumbprint is committed in the reusable WAP.'
    }
    else {
        Add-Result `
            -Status Pass `
            -Check 'Signing' `
            -Message 'No certificate thumbprint is committed.'
    }

    $signingValue = Get-XmlProperty `
        -Document $wapProject `
        -Name 'AppxPackageSigningEnabled'
    if ($signingValue -and $signingValue.Equals(
            'false',
            [StringComparison]::OrdinalIgnoreCase)) {
        Add-Result `
            -Status Pass `
            -Check 'Signing' `
            -Message 'Reusable WAP defaults to unsigned output.'
    }
    else {
        Add-Result `
            -Status Warning `
            -Check 'Signing' `
            -Message 'Reusable WAP does not explicitly default AppxPackageSigningEnabled to false.'
    }

    $publishAotNodes = @(
        $wapProject.SelectNodes("//*[local-name()='PublishAot']")
    )
    $unconditionalAot = @(
        $publishAotNodes |
            Where-Object {
                $_.InnerText.Trim().Equals(
                    'true',
                    [StringComparison]::OrdinalIgnoreCase) -and
                [string]::IsNullOrWhiteSpace($_.GetAttribute('Condition'))
            }
    )
    if ($unconditionalAot.Count -ne 0) {
        Add-Result `
            -Status Fail `
            -Check 'Native AOT policy' `
            -Message 'The WAP enables Native AOT unconditionally.'
    }
    else {
        Add-Result `
            -Status Pass `
            -Check 'Native AOT policy' `
            -Message 'The WAP does not enable Native AOT unconditionally.'
    }

    $buildToolsReference = $wapProject.SelectSingleNode(
        "//*[local-name()='PackageReference' and @Include='Microsoft.Windows.SDK.BuildTools']")
    if ($null -eq $buildToolsReference) {
        Add-Result `
            -Status Fail `
            -Check 'Windows SDK build tools' `
            -Message 'The WAP does not reference Microsoft.Windows.SDK.BuildTools.'
    }
    else {
        Add-Result `
            -Status Pass `
            -Check 'Windows SDK build tools' `
            -Message 'The WAP references Microsoft.Windows.SDK.BuildTools.'
    }

    $directoryPackagesPath = Join-Path $RepositoryRoot 'Directory.Packages.props'
    if (Test-Path -LiteralPath $directoryPackagesPath -PathType Leaf) {
        [xml] $directoryPackages = Get-Content -LiteralPath $directoryPackagesPath -Raw
        $centralManagementValue = Get-XmlProperty `
            -Document $directoryPackages `
            -Name 'ManagePackageVersionsCentrally'
        if ($centralManagementValue -and
            $centralManagementValue.Equals(
                'true',
                [StringComparison]::OrdinalIgnoreCase)) {
            $centralBuildToolsVersion = $directoryPackages.SelectSingleNode(
                "//*[local-name()='PackageVersion' and (@Include='Microsoft.Windows.SDK.BuildTools' or @Update='Microsoft.Windows.SDK.BuildTools')]")
            if ($null -eq $centralBuildToolsVersion) {
                Add-Result `
                    -Status Fail `
                    -Check 'Windows SDK build tools' `
                    -Message 'Central package management is enabled but Microsoft.Windows.SDK.BuildTools has no PackageVersion.'
            }
            else {
                Add-Result `
                    -Status Pass `
                    -Check 'Windows SDK build tools' `
                    -Message 'Microsoft.Windows.SDK.BuildTools is centrally versioned.'
            }
        }
    }

    if ($null -ne $appProjectPath -and
        (Test-Path -LiteralPath $appProjectPath -PathType Leaf)) {
        [xml] $appProject = Get-Content -LiteralPath $appProjectPath -Raw
        $appProjectText = Get-Content -LiteralPath $appProjectPath -Raw
        $appProjectDirectory = Split-Path -Parent $appProjectPath

        $singleProjectProperties = @(
            'AppxManifest',
            'EnableMsixTooling',
            'GenerateAppInstallerFile',
            'GenerateAppxPackageOnBuild',
            'GeneratePackageLocally',
            'AppxPackageSigningEnabled',
            'AppxPackageSigningTimestampDigestAlgorithm',
            'GenerateTemporaryStoreCertificate',
            'PackageCertificateThumbprint',
            'AppxAutoIncrementPackageRevision',
            'AppxPackageDir',
            'AppxBundle',
            'AppxBundlePlatforms',
            'AppxSymbolPackageEnabled',
            'AppxPackageIncludePrivateSymbols',
            'GenerateTestArtifacts',
            'HoursBetweenUpdateChecks'
        )
        $remainingProperties = @(
            $singleProjectProperties |
                Where-Object {
                    $null -ne $appProject.SelectSingleNode(
                        "//*[local-name()='$_']")
                }
        )
        $msixCapability = $appProject.SelectSingleNode(
            "//*[local-name()='ProjectCapability' and @Include='Msix']")
        $packageMenu = $appProject.SelectSingleNode(
            "//*[local-name()='HasPackageAndPublishMenu']")
        if ($null -ne $msixCapability) {
            $remainingProperties += 'ProjectCapability:Msix'
        }
        if ($null -ne $packageMenu) {
            $remainingProperties += 'HasPackageAndPublishMenu'
        }

        if ($remainingProperties.Count -ne 0) {
            Add-Result `
                -Status Fail `
                -Check 'Single-project ownership' `
                -Message "Application still owns WAP packaging settings: $($remainingProperties -join ', ')."
        }
        else {
            Add-Result `
                -Status Pass `
                -Check 'Single-project ownership' `
                -Message 'No stale single-project MSIX settings were found.'
        }

        foreach ($sharedMapping in @(
                [pscustomobject]@{
                    Property = 'TargetFramework'
                    Token    = '$(CmdPalTargetFramework)'
                },
                [pscustomobject]@{
                    Property = 'TargetPlatformMinVersion'
                    Token    = '$(CmdPalTargetPlatformMinVersion)'
                },
                [pscustomobject]@{
                    Property = 'SupportedOSPlatformVersion'
                    Token    = '$(CmdPalTargetPlatformMinVersion)'
                },
                [pscustomobject]@{
                    Property = 'RuntimeIdentifiers'
                    Token    = '$(CmdPalRuntimeIdentifiers)'
                })) {
            $sharedPropertyValues = @(
                $appProject.SelectNodes(
                    "//*[local-name()='$($sharedMapping.Property)']") |
                    ForEach-Object { $_.InnerText.Trim() }
                if ($null -ne $directoryBuildProps) {
                    $directoryBuildProps.SelectNodes(
                        "//*[local-name()='$($sharedMapping.Property)']") |
                        ForEach-Object { $_.InnerText.Trim() }
                }
            )
            if ($sharedPropertyValues -contains $sharedMapping.Token) {
                Add-Result `
                    -Status Pass `
                    -Check 'Shared application properties' `
                    -Message "$($sharedMapping.Property) consumes $($sharedMapping.Token)."
            }
            else {
                Add-Result `
                    -Status Warning `
                    -Check 'Shared application properties' `
                    -Message "$($sharedMapping.Property) does not consume $($sharedMapping.Token)."
            }
        }

        $publishProfiles = @{
            x64   = Join-Path $appProjectDirectory 'Properties\PublishProfiles\win-x64.pubxml'
            ARM64 = Join-Path $appProjectDirectory 'Properties\PublishProfiles\win-arm64.pubxml'
        }
        foreach ($profileEntry in $publishProfiles.GetEnumerator()) {
            if (-not (Test-Path -LiteralPath $profileEntry.Value -PathType Leaf)) {
                Add-Result `
                    -Status Fail `
                    -Check 'Publish profiles' `
                    -Message "Missing $($profileEntry.Key) publish profile '$($profileEntry.Value)'."
                continue
            }

            [xml] $publishProfile = Get-Content -LiteralPath $profileEntry.Value -Raw
            $singleFileValue = Get-XmlProperty `
                -Document $publishProfile `
                -Name 'PublishSingleFile'
            $selfContainedValue = Get-XmlProperty `
                -Document $publishProfile `
                -Name 'SelfContained'
            if ($singleFileValue -and $singleFileValue.Equals(
                    'true',
                    [StringComparison]::OrdinalIgnoreCase)) {
                Add-Result `
                    -Status Fail `
                    -Check 'Publish profiles' `
                    -Message "$($profileEntry.Key) enables PublishSingleFile."
            }
            else {
                Add-Result `
                    -Status Pass `
                    -Check 'Publish profiles' `
                    -Message "$($profileEntry.Key) exists and does not enable PublishSingleFile."
            }
            if (-not $selfContainedValue -or
                -not $selfContainedValue.Equals(
                    'true',
                    [StringComparison]::OrdinalIgnoreCase)) {
                Add-Result `
                    -Status Warning `
                    -Check 'Publish profiles' `
                    -Message "$($profileEntry.Key) is not explicitly self-contained; review the WAP framework dependency policy."
            }
            else {
                Add-Result `
                    -Status Pass `
                    -Check 'Publish profiles' `
                    -Message "$($profileEntry.Key) explicitly publishes self-contained."
            }
        }

        if ($null -ne $manifestPath -and
            (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            [xml] $manifest = Get-Content -LiteralPath $manifestPath -Raw
            $manifestText = Get-Content -LiteralPath $manifestPath -Raw
            $identity = $manifest.SelectSingleNode("//*[local-name()='Identity']")
            if ($null -eq $identity -or
                [string]::IsNullOrWhiteSpace($identity.GetAttribute('Name')) -or
                [string]::IsNullOrWhiteSpace($identity.GetAttribute('Publisher')) -or
                [string]::IsNullOrWhiteSpace($identity.GetAttribute('Version'))) {
                Add-Result `
                    -Status Fail `
                    -Check 'Package identity' `
                    -Message 'Manifest identity name, publisher, or version is missing.'
            }
            else {
                Add-Result `
                    -Status Pass `
                    -Check 'Package identity' `
                    -Message "Identity '$($identity.GetAttribute('Name'))' has publisher and version."
            }

            $application = $manifest.SelectSingleNode(
                "//*[local-name()='Application']")
            if ($null -eq $application -or
                $application.GetAttribute('Executable') -ne '$targetnametoken$.exe' -or
                $application.GetAttribute('EntryPoint') -ne '$targetentrypoint$') {
                Add-Result `
                    -Status Fail `
                    -Check 'Application entry point' `
                    -Message 'Source manifest Application must use the WAP target-name and entry-point tokens.'
            }
            else {
                Add-Result `
                    -Status Pass `
                    -Check 'Application entry point' `
                    -Message 'Source manifest uses the WAP application tokens.'
            }

            $configuredTargetVersion = Get-XmlProperty `
                -Document $extensionProps `
                -Name 'CmdPalTargetPlatformVersion'
            $configuredMinimumVersion = Get-XmlProperty `
                -Document $extensionProps `
                -Name 'CmdPalTargetPlatformMinVersion'
            $targetDeviceFamilies = @(
                $manifest.SelectNodes("//*[local-name()='TargetDeviceFamily']")
            )
            $targetVersionMismatches = @(
                $targetDeviceFamilies |
                    Where-Object {
                        $_.GetAttribute('MinVersion') -ne $configuredMinimumVersion -or
                        $_.GetAttribute('MaxVersionTested') -ne $configuredTargetVersion
                    }
            )
            if ($targetDeviceFamilies.Count -eq 0) {
                Add-Result `
                    -Status Fail `
                    -Check 'Target device families' `
                    -Message 'Manifest has no TargetDeviceFamily declarations.'
            }
            elseif ($targetVersionMismatches.Count -ne 0) {
                Add-Result `
                    -Status Warning `
                    -Check 'Target device families' `
                    -Message 'One or more manifest target-family versions differ from the shared target versions.'
            }
            else {
                Add-Result `
                    -Status Pass `
                    -Check 'Target device families' `
                    -Message 'Manifest target-family versions match the shared contract.'
            }

            $exeServer = $manifest.SelectSingleNode("//*[local-name()='ExeServer']")
            $comClass = $manifest.SelectSingleNode("//*[local-name()='Class']")
            $createInstance = $manifest.SelectSingleNode(
                "//*[local-name()='CreateInstance']")

            if ($null -eq $exeServer -or
                $null -eq $comClass -or
                $null -eq $createInstance) {
                Add-Result `
                    -Status Fail `
                    -Check 'CmdPal registration' `
                    -Message 'Manifest is missing ExeServer, COM Class, or CreateInstance.'
            }
            else {
                $comClassId = [string] $comClass.GetAttribute('Id')
                $createInstanceId = [string] $createInstance.GetAttribute('ClassId')
                if (-not $comClassId.Equals(
                        $createInstanceId,
                        [StringComparison]::OrdinalIgnoreCase)) {
                    Add-Result `
                        -Status Fail `
                        -Check 'Provider CLSID' `
                        -Message "COM class '$comClassId' differs from CreateInstance '$createInstanceId'."
                }
                else {
                    Add-Result `
                        -Status Pass `
                        -Check 'Provider CLSID' `
                        -Message "Manifest CLSIDs match: '$comClassId'."
                }

                $providerGuidPattern =
                    '\[\s*Guid\s*\(\s*"' +
                    [regex]::Escape($comClassId) +
                    '"\s*\)\s*\]'
                $matchingGuidSource = @(
                    Get-ChildItem -LiteralPath $appProjectDirectory -Recurse -File -Filter '*.cs' |
                        Where-Object {
                            [IO.Path]::GetRelativePath(
                                $appProjectDirectory,
                                $_.FullName) -notmatch '(^|[\\/])(bin|obj)[\\/]'
                        } |
                        ForEach-Object {
                            Select-String `
                                -LiteralPath $_.FullName `
                                -Pattern $providerGuidPattern
                        }
                )
                if ($matchingGuidSource.Count -eq 0) {
                    Add-Result `
                        -Status Fail `
                        -Check 'Provider CLSID' `
                        -Message "Provider CLSID '$comClassId' was not found in application C# source."
                }
                else {
                    Add-Result `
                        -Status Pass `
                        -Check 'Provider CLSID' `
                        -Message 'Manifest provider CLSID matches a C# [Guid] attribute.'
                }

                $configuredExecutable = Get-XmlProperty `
                    -Document $extensionProps `
                    -Name 'CmdPalPackageExecutable'
                $manifestExecutable = [string] $exeServer.GetAttribute('Executable')
                if (-not $manifestExecutable.Equals(
                        $configuredExecutable,
                        [StringComparison]::OrdinalIgnoreCase)) {
                    Add-Result `
                        -Status Fail `
                        -Check 'COM executable' `
                        -Message "Manifest executable '$manifestExecutable' differs from '$configuredExecutable'."
                }
                else {
                    Add-Result `
                        -Status Pass `
                        -Check 'COM executable' `
                        -Message "COM executable matches '$configuredExecutable'."
                }

                if ($exeServer.GetAttribute('Arguments') -ne '-RegisterProcessAsComServer') {
                    Add-Result `
                        -Status Warning `
                        -Check 'COM activation arguments' `
                        -Message 'ExeServer arguments differ from -RegisterProcessAsComServer.'
                }
                else {
                    Add-Result `
                        -Status Pass `
                        -Check 'COM activation arguments' `
                        -Message 'COM activation arguments match the CmdPal convention.'
                }
            }

            $appExtension = $manifest.SelectSingleNode(
                "//*[local-name()='AppExtension' and @Name='com.microsoft.commandpalette']")
            if ($null -eq $appExtension) {
                Add-Result `
                    -Status Fail `
                    -Check 'CmdPal app extension' `
                    -Message 'The com.microsoft.commandpalette app extension is missing.'
            }
            else {
                Add-Result `
                    -Status Pass `
                    -Check 'CmdPal app extension' `
                    -Message 'The CmdPal app extension is declared.'
            }

            $commandsInterface = $manifest.SelectSingleNode(
                "//*[local-name()='SupportedInterfaces']/*[local-name()='Commands']")
            if ($null -eq $commandsInterface) {
                Add-Result `
                    -Status Fail `
                    -Check 'CmdPal supported interfaces' `
                    -Message 'The Commands supported interface is missing.'
            }
            else {
                Add-Result `
                    -Status Pass `
                    -Check 'CmdPal supported interfaces' `
                    -Message 'The Commands supported interface is declared.'
            }

            $runFullTrustCapability = $manifest.SelectSingleNode(
                "//*[local-name()='Capabilities']/*[@Name='runFullTrust']")
            if ($null -eq $runFullTrustCapability) {
                Add-Result `
                    -Status Fail `
                    -Check 'Package capabilities' `
                    -Message 'The runFullTrust capability is missing.'
            }
            else {
                Add-Result `
                    -Status Pass `
                    -Check 'Package capabilities' `
                    -Message 'The runFullTrust capability is declared.'
            }

            $assetReferences = [Collections.Generic.HashSet[string]]::new(
                [StringComparer]::OrdinalIgnoreCase)
            foreach ($attribute in $manifest.SelectNodes('//@*')) {
                $attributeValue = [string] $attribute.Value
                if ($attributeValue.StartsWith(
                        'Assets\',
                        [StringComparison]::OrdinalIgnoreCase)) {
                    [void] $assetReferences.Add($attributeValue)
                }
            }
            foreach ($element in $manifest.SelectNodes("//*[not(*)]")) {
                $elementValue = [string] $element.InnerText
                if ($elementValue.StartsWith(
                        'Assets\',
                        [StringComparison]::OrdinalIgnoreCase)) {
                    [void] $assetReferences.Add($elementValue)
                }
            }

            foreach ($assetReference in $assetReferences) {
                if (Test-PackageAsset `
                        -AssetPath $assetReference `
                        -PackageProjectDirectory $packageProjectDirectory) {
                    Add-Result `
                        -Status Pass `
                        -Check 'Manifest assets' `
                        -Message "Resolved '$assetReference'."
                }
                else {
                    Add-Result `
                        -Status Fail `
                        -Check 'Manifest assets' `
                        -Message "Manifest asset was not found: '$assetReference'."
                }
            }

            if ($manifestText.Contains('ms-resource:')) {
                $priResources = @(
                    Get-ChildItem `
                        -LiteralPath (Join-Path $packageProjectDirectory 'Strings') `
                        -Recurse `
                        -File `
                        -Filter 'Resources.resw' `
                        -ErrorAction SilentlyContinue
                )
                if ($priResources.Count -eq 0) {
                    Add-Result `
                        -Status Fail `
                        -Check 'PRI resources' `
                        -Message 'Manifest uses ms-resource values, but no Strings/**/Resources.resw files exist.'
                }
                else {
                    Add-Result `
                        -Status Pass `
                        -Check 'PRI resources' `
                        -Message "Found $($priResources.Count) package PRI resource files."
                }
            }
            else {
                Add-Result `
                    -Status Pass `
                    -Check 'PRI resources' `
                    -Message 'Manifest does not use ms-resource values.'
            }
        }
    }

    if (-not (Test-Path -LiteralPath $directoryBuildPropsPath -PathType Leaf)) {
        Add-Result `
            -Status Warning `
            -Check 'Directory.Build.props import' `
            -Message 'Directory.Build.props was not found.'
    }
    elseif ($directoryBuildPropsText.Contains('CmdPal.Extension.props')) {
        Add-Result `
            -Status Pass `
            -Check 'Directory.Build.props import' `
            -Message 'Directory.Build.props imports the shared extension contract.'
    }
    else {
        Add-Result `
            -Status Fail `
            -Check 'Directory.Build.props import' `
            -Message 'Directory.Build.props does not import CmdPal.Extension.props.'
    }

    $solutionFiles = @(
        Get-ChildItem -LiteralPath $RepositoryRoot -File |
            Where-Object { $_.Extension -in @('.sln', '.slnx') }
    )
    $solutionContainsWap = $false
    $solutionDeploysWap = $false
    $wapRelativePath = [IO.Path]::GetRelativePath(
        $RepositoryRoot,
        $WapProjectPath).Replace('\', '/')
    foreach ($solutionFile in $solutionFiles) {
        $solutionText = Get-Content -LiteralPath $solutionFile.FullName -Raw
        if ($solutionText.Replace('\', '/').Contains($wapRelativePath)) {
            $solutionContainsWap = $true
            if ($solutionFile.Extension -eq '.slnx') {
                [xml] $solution = $solutionText
                $solutionProject = $solution.SelectSingleNode(
                    "//*[local-name()='Project' and translate(@Path, '\', '/')='$wapRelativePath']")
                if ($null -ne $solutionProject -and
                    $null -ne $solutionProject.SelectSingleNode("*[local-name()='Deploy']")) {
                    $solutionDeploysWap = $true
                }
            }
            elseif ($solutionText -match '\.Deploy\.0\s*=') {
                $solutionDeploysWap = $true
            }
        }
    }

    if (-not $solutionContainsWap) {
        Add-Result `
            -Status Fail `
            -Check 'Solution wiring' `
            -Message 'No root solution contains the WAP project.'
    }
    elseif (-not $solutionDeploysWap) {
        Add-Result `
            -Status Fail `
            -Check 'Solution wiring' `
            -Message 'The WAP is in a solution but has no deploy mapping.'
    }
    else {
        Add-Result `
            -Status Pass `
            -Check 'Solution wiring' `
            -Message 'The WAP is present and enabled for deployment.'
    }
}

$failureCount = @($results | Where-Object Status -eq 'Fail').Count
$warningCount = @($results | Where-Object Status -eq 'Warning').Count
$summary = [pscustomobject]@{
    Status       = if ($failureCount -eq 0) { 'Pass' } else { 'Fail' }
    FailureCount = $failureCount
    WarningCount = $warningCount
    Checks       = $results
}

if ($OutputFormat -eq 'Json') {
    $summary | ConvertTo-Json -Depth 5
}
else {
    foreach ($result in $results) {
        '[{0}] {1}: {2}' -f $result.Status.ToUpperInvariant(), $result.Check, $result.Message
    }
    ''
    "Result: $($summary.Status); failures: $failureCount; warnings: $warningCount"
}

if ($failureCount -ne 0) {
    exit 1
}
