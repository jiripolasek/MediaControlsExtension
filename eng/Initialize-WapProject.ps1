<#
.SYNOPSIS
Scaffolds a Windows Application Packaging Project for a Command Palette extension.

.DESCRIPTION
Creates a WAP, a repository-specific shared MSBuild property contract, and a
package manifest. For migrations, the existing single-project manifest is
copied, its manifest-referenced assets and PRI strings are copied beside the
WAP, and its COM server executable is adjusted for the WAP payload layout. The
source manifest, resources, and application project are never modified or
deleted.

The script refuses to overwrite existing output. Run Test-WapProject.ps1 after
the application project and solution have been migrated.

.EXAMPLE
.\eng\Initialize-WapProject.ps1 `
  -AppProjectPath .\src\ExampleExtension\ExampleExtension.csproj `
  -WhatIf

.EXAMPLE
.\eng\Initialize-WapProject.ps1 `
  -AppProjectPath .\src\ExampleExtension\ExampleExtension.csproj

.EXAMPLE
.\eng\Initialize-WapProject.ps1 `
  -AppProjectPath .\src\ExampleExtension\ExampleExtension.csproj `
  -CreateManifest `
  -PackageIdentityName Contoso.ExampleForCommandPalette `
  -Publisher 'CN=00000000-0000-0000-0000-000000000000' `
  -PublisherDisplayName Contoso `
  -DisplayName 'Example for Command Palette' `
  -ProviderClassId 00000000-0000-0000-0000-000000000001
#>

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory)]
    [string] $AppProjectPath,

    [Parameter()]
    [string] $RepositoryRoot,

    [Parameter()]
    [string] $PackageProjectDirectory,

    [Parameter()]
    [string] $ExtensionPropsPath,

    [Parameter()]
    [string] $SourceManifestPath,

    [Parameter()]
    [string] $ArtifactsPath = 'artifacts',

    [Parameter()]
    [string] $TargetFramework,

    [Parameter()]
    [string] $TargetPlatformVersion,

    [Parameter()]
    [string] $TargetPlatformMinVersion,

    [Parameter()]
    [string] $WindowsSdkBuildToolsVersion,

    [Parameter()]
    [switch] $SupportsNativeAot,

    [Parameter()]
    [switch] $CreateManifest,

    [Parameter()]
    [string] $PackageIdentityName,

    [Parameter()]
    [string] $Publisher,

    [Parameter()]
    [string] $PublisherDisplayName,

    [Parameter()]
    [string] $DisplayName,

    [Parameter()]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string] $PackageVersion = '1.0.0.0',

    [Parameter()]
    [Guid] $ProviderClassId = [Guid]::Empty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ProjectProperty {
    param(
        [Parameter(Mandatory)]
        [xml] $Project,

        [Parameter(Mandatory)]
        [string] $Name
    )

    $values = @(
        $Project.SelectNodes("//*[local-name()='$Name']") |
            ForEach-Object { $_.InnerText.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($values.Count -eq 0) {
        return $null
    }

    return [string] $values[0]
}

function Resolve-InputPath {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $BasePath
    )

    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }

    return [IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

function Assert-PathWithinRoot {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $Root,

        [Parameter(Mandatory)]
        [string] $Description
    )

    $resolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description must be inside repository root '$resolvedRoot': '$resolvedPath'."
    }
}

function Get-RepositoryRelativePath {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $Root
    )

    return [IO.Path]::GetRelativePath($Root, $Path).Replace('/', '\')
}

function Get-XmlEscapedValue {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $Value
    )

    return [Security.SecurityElement]::Escape($Value)
}

function Expand-Template {
    param(
        [Parameter(Mandatory)]
        [string] $Template,

        [Parameter(Mandatory)]
        [Collections.IDictionary] $Values
    )

    $expanded = $Template
    foreach ($key in $Values.Keys) {
        $expanded = $expanded.Replace("__${key}__", [string] $Values[$key])
    }

    $remainingTokens = @(
        [regex]::Matches($expanded, '__[A-Z0-9_]+__') |
            ForEach-Object { $_.Value } |
            Sort-Object -Unique
    )
    if ($remainingTokens.Count -ne 0) {
        throw "Template expansion left unresolved tokens: $($remainingTokens -join ', ')."
    }

    return $expanded
}

function Write-Utf8File {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $Content
    )

    $normalizedContent = $Content.Replace("`r`n", "`n").Replace("`n", "`r`n")
    $utf8WithoutBom = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText($Path, $normalizedContent, $utf8WithoutBom)
}

function Get-PackageAssetFiles {
    param(
        [Parameter(Mandatory)]
        [xml] $Manifest,

        [Parameter(Mandatory)]
        [string] $ApplicationProjectDirectory
    )

    $assetReferences = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($attribute in $Manifest.SelectNodes('//@*')) {
        $value = ([string] $attribute.Value).Trim()
        if ($value.StartsWith('Assets\', [StringComparison]::OrdinalIgnoreCase)) {
            [void] $assetReferences.Add($value)
        }
    }
    foreach ($element in $Manifest.SelectNodes("//*[not(*)]")) {
        $value = ([string] $element.InnerText).Trim()
        if ($value.StartsWith('Assets\', [StringComparison]::OrdinalIgnoreCase)) {
            [void] $assetReferences.Add($value)
        }
    }

    $sourceFiles = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($assetReference in $assetReferences) {
        $relativeAssetPath = $assetReference.Replace('/', '\')
        if ([IO.Path]::IsPathRooted($relativeAssetPath)) {
            throw "Package asset path must be relative: '$assetReference'."
        }

        $candidate = [IO.Path]::GetFullPath(
            (Join-Path $ApplicationProjectDirectory $relativeAssetPath))
        Assert-PathWithinRoot `
            -Path $candidate `
            -Root $ApplicationProjectDirectory `
            -Description 'Package asset'

        $candidateDirectory = Split-Path -Parent $candidate
        if (-not (Test-Path -LiteralPath $candidateDirectory -PathType Container)) {
            throw "Package asset directory was not found: '$candidateDirectory'."
        }

        $fileName = [IO.Path]::GetFileNameWithoutExtension($candidate)
        $extension = [IO.Path]::GetExtension($candidate)
        $matchingFiles = @(
            Get-ChildItem -LiteralPath $candidateDirectory -File |
                Where-Object {
                    ($_.BaseName.Equals(
                            $fileName,
                            [StringComparison]::OrdinalIgnoreCase) -or
                        $_.BaseName.StartsWith(
                            "$fileName.",
                            [StringComparison]::OrdinalIgnoreCase)) -and
                    $_.Extension.Equals(
                        $extension,
                        [StringComparison]::OrdinalIgnoreCase)
                }
        )
        if ($matchingFiles.Count -eq 0) {
            throw "Package asset '$assetReference' and its qualified variants were not found."
        }

        $relativeDirectory = [IO.Path]::GetDirectoryName($relativeAssetPath)
        foreach ($matchingFile in $matchingFiles) {
            $destinationRelativePath = if ([string]::IsNullOrWhiteSpace(
                    $relativeDirectory)) {
                $matchingFile.Name
            }
            else {
                Join-Path $relativeDirectory $matchingFile.Name
            }
            $sourceFiles[$destinationRelativePath] = $matchingFile.FullName
        }
    }

    foreach ($entry in $sourceFiles.GetEnumerator()) {
        [pscustomobject]@{
            SourcePath   = $entry.Value
            RelativePath = $entry.Key
        }
    }
}

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) {
    throw "Repository root was not found: '$RepositoryRoot'."
}

$AppProjectPath = Resolve-InputPath -Path $AppProjectPath -BasePath $RepositoryRoot
Assert-PathWithinRoot -Path $AppProjectPath -Root $RepositoryRoot -Description 'Application project'
if (-not (Test-Path -LiteralPath $AppProjectPath -PathType Leaf)) {
    throw "Application project was not found: '$AppProjectPath'."
}
if ([IO.Path]::GetExtension($AppProjectPath) -ne '.csproj') {
    throw "Application project must be a .csproj file: '$AppProjectPath'."
}

[xml] $appProject = Get-Content -LiteralPath $AppProjectPath -Raw
$appProjectDirectory = Split-Path -Parent $AppProjectPath
$appProjectFileName = [IO.Path]::GetFileNameWithoutExtension($AppProjectPath)
$assemblyName = Get-ProjectProperty -Project $appProject -Name 'AssemblyName'
if ([string]::IsNullOrWhiteSpace($assemblyName)) {
    $assemblyName = $appProjectFileName
}

if ([string]::IsNullOrWhiteSpace($TargetFramework)) {
    $TargetFramework = Get-ProjectProperty -Project $appProject -Name 'TargetFramework'
}
if ([string]::IsNullOrWhiteSpace($TargetFramework) -or $TargetFramework.Contains('$(')) {
    throw 'TargetFramework could not be read as a literal value. Pass -TargetFramework explicitly.'
}

if ([string]::IsNullOrWhiteSpace($TargetPlatformVersion)) {
    $targetFrameworkMatch = [regex]::Match(
        $TargetFramework,
        'windows(?<version>\d+\.\d+\.\d+\.\d+)',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($targetFrameworkMatch.Success) {
        $TargetPlatformVersion = $targetFrameworkMatch.Groups['version'].Value
    }
    else {
        $TargetPlatformVersion = Get-ProjectProperty -Project $appProject -Name 'TargetPlatformVersion'
    }
}
if ([string]::IsNullOrWhiteSpace($TargetPlatformVersion)) {
    throw 'Target platform version could not be inferred. Pass -TargetPlatformVersion explicitly.'
}

if ([string]::IsNullOrWhiteSpace($TargetPlatformMinVersion)) {
    $TargetPlatformMinVersion = Get-ProjectProperty -Project $appProject -Name 'TargetPlatformMinVersion'
}
if ([string]::IsNullOrWhiteSpace($TargetPlatformMinVersion)) {
    $TargetPlatformMinVersion = Get-ProjectProperty -Project $appProject -Name 'SupportedOSPlatformVersion'
}
if ([string]::IsNullOrWhiteSpace($TargetPlatformMinVersion)) {
    throw 'Target platform minimum version could not be inferred. Pass -TargetPlatformMinVersion explicitly.'
}

$runtimeIdentifiers = Get-ProjectProperty -Project $appProject -Name 'RuntimeIdentifiers'
if ([string]::IsNullOrWhiteSpace($runtimeIdentifiers)) {
    $runtimeIdentifiers = 'win-x64;win-arm64'
}

$defaultLanguage = Get-ProjectProperty -Project $appProject -Name 'NeutralLanguage'
if ([string]::IsNullOrWhiteSpace($defaultLanguage)) {
    $defaultLanguage = Get-ProjectProperty -Project $appProject -Name 'DefaultLanguage'
}
if ([string]::IsNullOrWhiteSpace($defaultLanguage)) {
    $defaultLanguage = 'en-US'
}

if ([string]::IsNullOrWhiteSpace($PackageProjectDirectory)) {
    $PackageProjectDirectory = Join-Path `
        (Split-Path -Parent $appProjectDirectory) `
        "$([IO.Path]::GetFileName($appProjectDirectory)).Package"
}
else {
    $PackageProjectDirectory = Resolve-InputPath `
        -Path $PackageProjectDirectory `
        -BasePath $RepositoryRoot
}
$PackageProjectDirectory = [IO.Path]::GetFullPath($PackageProjectDirectory)
Assert-PathWithinRoot `
    -Path $PackageProjectDirectory `
    -Root $RepositoryRoot `
    -Description 'Package project directory'

if ([string]::IsNullOrWhiteSpace($ExtensionPropsPath)) {
    $ExtensionPropsPath = Join-Path $RepositoryRoot 'eng\CmdPal.Extension.props'
}
else {
    $ExtensionPropsPath = Resolve-InputPath -Path $ExtensionPropsPath -BasePath $RepositoryRoot
}
$ExtensionPropsPath = [IO.Path]::GetFullPath($ExtensionPropsPath)
Assert-PathWithinRoot `
    -Path $ExtensionPropsPath `
    -Root $RepositoryRoot `
    -Description 'Extension property contract'

if ([IO.Path]::IsPathRooted($ArtifactsPath)) {
    throw 'ArtifactsPath must be repository-relative.'
}
$artifactsFullPath = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot $ArtifactsPath))
Assert-PathWithinRoot -Path $artifactsFullPath -Root $RepositoryRoot -Description 'Artifacts path'

$packageProjectPath = Join-Path $PackageProjectDirectory "$assemblyName.Package.wapproj"
$packageManifestPath = Join-Path $PackageProjectDirectory 'Package.appxmanifest'
$packageStoreAssociationPath = Join-Path $PackageProjectDirectory 'Package.StoreAssociation.xml'

foreach ($outputPath in @($packageProjectPath, $packageManifestPath, $ExtensionPropsPath)) {
    if (Test-Path -LiteralPath $outputPath) {
        throw "Refusing to overwrite existing output: '$outputPath'."
    }
}
if (Test-Path -LiteralPath $PackageProjectDirectory -PathType Container) {
    $existingPackageProjectContent = @(Get-ChildItem -LiteralPath $PackageProjectDirectory -Force)
    if ($existingPackageProjectContent.Count -ne 0) {
        throw "Package project directory is not empty: '$PackageProjectDirectory'."
    }
}

if ([string]::IsNullOrWhiteSpace($SourceManifestPath)) {
    $SourceManifestPath = Join-Path $appProjectDirectory 'Package.appxmanifest'
}
else {
    $SourceManifestPath = Resolve-InputPath -Path $SourceManifestPath -BasePath $RepositoryRoot
}

if ($CreateManifest) {
    if (Test-Path -LiteralPath $SourceManifestPath -PathType Leaf) {
        throw "CreateManifest was specified, but a source manifest exists at '$SourceManifestPath'."
    }

    $requiredNewManifestValues = [ordered]@{
        PackageIdentityName  = $PackageIdentityName
        Publisher           = $Publisher
        PublisherDisplayName = $PublisherDisplayName
        DisplayName         = $DisplayName
    }
    foreach ($entry in $requiredNewManifestValues.GetEnumerator()) {
        if ([string]::IsNullOrWhiteSpace([string] $entry.Value)) {
            throw "-$($entry.Key) is required when -CreateManifest is used."
        }
    }
    if ($ProviderClassId -eq [Guid]::Empty) {
        throw '-ProviderClassId is required when -CreateManifest is used.'
    }
}
elseif (-not (Test-Path -LiteralPath $SourceManifestPath -PathType Leaf)) {
    throw "Source package manifest was not found: '$SourceManifestPath'. Use -CreateManifest for a new project."
}

$directoryPackagesPath = Join-Path $RepositoryRoot 'Directory.Packages.props'
$centralPackageManagementEnabled = $false
$centrallyManagesBuildTools = $false
if (Test-Path -LiteralPath $directoryPackagesPath -PathType Leaf) {
    [xml] $directoryPackages = Get-Content -LiteralPath $directoryPackagesPath -Raw
    $managePackageVersionsCentrally = Get-ProjectProperty `
        -Project $directoryPackages `
        -Name 'ManagePackageVersionsCentrally'
    $centralPackageManagementEnabled =
        $managePackageVersionsCentrally -and
        $managePackageVersionsCentrally.Equals(
            'true',
            [StringComparison]::OrdinalIgnoreCase)
    $centralBuildToolsReference = $directoryPackages.SelectSingleNode(
        "//*[local-name()='PackageVersion' and (@Include='Microsoft.Windows.SDK.BuildTools' or @Update='Microsoft.Windows.SDK.BuildTools')]")
    $centrallyManagesBuildTools =
        $centralPackageManagementEnabled -and
        $null -ne $centralBuildToolsReference
}
if ($centralPackageManagementEnabled -and -not $centrallyManagesBuildTools) {
    throw @'
Directory.Packages.props enables central package management but does not declare
Microsoft.Windows.SDK.BuildTools. Add its PackageVersion before scaffolding the
WAP.
'@
}
if ([string]::IsNullOrWhiteSpace($WindowsSdkBuildToolsVersion)) {
    $WindowsSdkBuildToolsVersion = Get-ProjectProperty `
        -Project $appProject `
        -Name 'WindowsSdkPackageVersion'
}
if (-not $centrallyManagesBuildTools -and
    [string]::IsNullOrWhiteSpace($WindowsSdkBuildToolsVersion)) {
    throw @'
Microsoft.Windows.SDK.BuildTools is not centrally versioned and no version
could be inferred. Pass -WindowsSdkBuildToolsVersion explicitly or add a
PackageVersion to Directory.Packages.props.
'@
}

$templateDirectory = Join-Path $PSScriptRoot 'templates\wap'
$extensionPropsTemplatePath = Join-Path $templateDirectory 'CmdPal.Extension.props.template'
$wapTemplatePath = Join-Path $templateDirectory 'Package.wapproj.template'
$manifestTemplatePath = Join-Path $templateDirectory 'Package.appxmanifest.template'
foreach ($templatePath in @(
        $extensionPropsTemplatePath,
        $wapTemplatePath,
        $manifestTemplatePath)) {
    if (-not (Test-Path -LiteralPath $templatePath -PathType Leaf)) {
        throw "Required WAP template was not found: '$templatePath'."
    }
}

$appProjectRelativePath = Get-RepositoryRelativePath `
    -Path $AppProjectPath `
    -Root $RepositoryRoot
$packageProjectRelativePath = Get-RepositoryRelativePath `
    -Path $packageProjectPath `
    -Root $RepositoryRoot
$packageManifestRelativePath = Get-RepositoryRelativePath `
    -Path $packageManifestPath `
    -Root $RepositoryRoot
$artifactsRelativePath = Get-RepositoryRelativePath `
    -Path $artifactsFullPath `
    -Root $RepositoryRoot
$repositoryRootRelativeToProps = [IO.Path]::GetRelativePath(
    (Split-Path -Parent $ExtensionPropsPath),
    $RepositoryRoot).Replace('/', '\')
$extensionPropsRelativeToPackage = [IO.Path]::GetRelativePath(
    $PackageProjectDirectory,
    $ExtensionPropsPath).Replace('/', '\')
$appProjectRelativeToPackage = [IO.Path]::GetRelativePath(
    $PackageProjectDirectory,
    $AppProjectPath).Replace('/', '\')

$packageExecutable = "$assemblyName\$assemblyName.exe"
$packageProjectGuid = [Guid]::NewGuid().ToString('D')

$extensionPropsTemplate = Get-Content -LiteralPath $extensionPropsTemplatePath -Raw
$extensionPropsContent = Expand-Template `
    -Template $extensionPropsTemplate `
    -Values ([ordered]@{
        REPOSITORY_ROOT_RELATIVE_PATH       = Get-XmlEscapedValue $repositoryRootRelativeToProps
        APP_PROJECT_RELATIVE_PATH           = Get-XmlEscapedValue $appProjectRelativePath
        APP_PROJECT_REFERENCE               = Get-XmlEscapedValue $appProjectRelativeToPackage
        ASSEMBLY_NAME                       = Get-XmlEscapedValue $assemblyName
        PACKAGE_PROJECT_RELATIVE_PATH       = Get-XmlEscapedValue $packageProjectRelativePath
        PACKAGE_MANIFEST_RELATIVE_PATH      = Get-XmlEscapedValue $packageManifestRelativePath
        PACKAGE_EXECUTABLE                  = Get-XmlEscapedValue $packageExecutable
        PACKAGE_PROJECT_GUID                = $packageProjectGuid
        TARGET_FRAMEWORK                    = Get-XmlEscapedValue $TargetFramework
        TARGET_PLATFORM_VERSION             = Get-XmlEscapedValue $TargetPlatformVersion
        TARGET_PLATFORM_MIN_VERSION         = Get-XmlEscapedValue $TargetPlatformMinVersion
        RUNTIME_IDENTIFIERS                 = Get-XmlEscapedValue $runtimeIdentifiers
        DEFAULT_LANGUAGE                    = Get-XmlEscapedValue $defaultLanguage
        ARTIFACTS_RELATIVE_PATH             = Get-XmlEscapedValue $artifactsRelativePath
        SUPPORTS_NATIVE_AOT                 = $SupportsNativeAot.IsPresent.ToString().ToLowerInvariant()
        WINDOWS_SDK_BUILD_TOOLS_VERSION     = Get-XmlEscapedValue ([string] $WindowsSdkBuildToolsVersion)
    })

$wapTemplate = Get-Content -LiteralPath $wapTemplatePath -Raw
$wapContent = Expand-Template `
    -Template $wapTemplate `
    -Values ([ordered]@{
        EXTENSION_PROPS_RELATIVE_PATH = Get-XmlEscapedValue $extensionPropsRelativeToPackage
    })

$manifestContent = $null
$preparedSourceManifest = $null
$packageResourceFiles = @()
if ($CreateManifest) {
    $manifestTemplate = Get-Content -LiteralPath $manifestTemplatePath -Raw
    $manifestContent = Expand-Template `
        -Template $manifestTemplate `
        -Values ([ordered]@{
            PACKAGE_IDENTITY_NAME        = Get-XmlEscapedValue $PackageIdentityName
            PUBLISHER                    = Get-XmlEscapedValue $Publisher
            PACKAGE_VERSION              = Get-XmlEscapedValue $PackageVersion
            DISPLAY_NAME                 = Get-XmlEscapedValue $DisplayName
            PUBLISHER_DISPLAY_NAME       = Get-XmlEscapedValue $PublisherDisplayName
            TARGET_PLATFORM_MIN_VERSION  = Get-XmlEscapedValue $TargetPlatformMinVersion
            TARGET_PLATFORM_VERSION      = Get-XmlEscapedValue $TargetPlatformVersion
            PACKAGE_EXECUTABLE           = Get-XmlEscapedValue $packageExecutable
            PROVIDER_CLASS_ID            = $ProviderClassId.ToString('D')
        })
}
else {
    [xml] $preparedSourceManifest = Get-Content -LiteralPath $SourceManifestPath -Raw
    $exeServer = $preparedSourceManifest.SelectSingleNode(
        "//*[local-name()='ExeServer']")
    if ($null -eq $exeServer) {
        throw "Source manifest has no com:ExeServer element: '$SourceManifestPath'."
    }

    $sourceExecutable = [string] $exeServer.GetAttribute('Executable')
    $singleProjectExecutable = "$assemblyName.exe"
    if (-not $sourceExecutable.Equals(
            $singleProjectExecutable,
            [StringComparison]::OrdinalIgnoreCase) -and
        -not $sourceExecutable.Equals(
            $packageExecutable,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw @"
The source COM executable '$sourceExecutable' is not the expected
'$singleProjectExecutable'. Review the manifest instead of rewriting it
automatically.
"@
    }

    $exeServer.SetAttribute('Executable', $packageExecutable)

    $packageResourceFiles = @(
        Get-PackageAssetFiles `
            -Manifest $preparedSourceManifest `
            -ApplicationProjectDirectory $appProjectDirectory
    )
    if ($preparedSourceManifest.OuterXml.Contains('ms-resource:')) {
        $sourceStringsDirectory = Join-Path $appProjectDirectory 'Strings'
        $sourcePriResources = @(
            Get-ChildItem `
                -LiteralPath $sourceStringsDirectory `
                -Recurse `
                -File `
                -Filter 'Resources.resw' `
                -ErrorAction SilentlyContinue
        )
        if ($sourcePriResources.Count -eq 0) {
            throw @"
The package manifest uses ms-resource values, but no package PRI resources were
found under '$sourceStringsDirectory'.
"@
        }

        $packageResourceFiles += @(
            $sourcePriResources |
                ForEach-Object {
                    [pscustomobject]@{
                        SourcePath   = $_.FullName
                        RelativePath = [IO.Path]::GetRelativePath(
                            $appProjectDirectory,
                            $_.FullName).Replace('/', '\')
                    }
                }
        )
    }
}

if ($PSCmdlet.ShouldProcess($PackageProjectDirectory, 'Create WAP project directory')) {
    New-Item -ItemType Directory -Path $PackageProjectDirectory -Force | Out-Null
}
$extensionPropsDirectory = Split-Path -Parent $ExtensionPropsPath
if ($PSCmdlet.ShouldProcess($extensionPropsDirectory, 'Create extension property directory')) {
    New-Item -ItemType Directory -Path $extensionPropsDirectory -Force | Out-Null
}

if ($PSCmdlet.ShouldProcess($ExtensionPropsPath, 'Create shared extension property contract')) {
    Write-Utf8File -Path $ExtensionPropsPath -Content $extensionPropsContent
}
if ($PSCmdlet.ShouldProcess($packageProjectPath, 'Create Windows Application Packaging Project')) {
    Write-Utf8File -Path $packageProjectPath -Content $wapContent
}

if ($CreateManifest) {
    if ($PSCmdlet.ShouldProcess($packageManifestPath, 'Create package manifest')) {
        Write-Utf8File -Path $packageManifestPath -Content $manifestContent
    }
}
elseif ($PSCmdlet.ShouldProcess(
        $packageManifestPath,
        "Copy package manifest from '$SourceManifestPath' and update COM executable")) {
    $xmlWriterSettings = [Xml.XmlWriterSettings]::new()
    $xmlWriterSettings.Encoding = [Text.UTF8Encoding]::new($false)
    $xmlWriterSettings.Indent = $true
    $xmlWriterSettings.NewLineChars = "`r`n"
    $xmlWriterSettings.NewLineHandling = [Xml.NewLineHandling]::Replace
    $xmlWriter = [Xml.XmlWriter]::Create($packageManifestPath, $xmlWriterSettings)
    try {
        $preparedSourceManifest.Save($xmlWriter)
    }
    finally {
        $xmlWriter.Dispose()
    }
}

foreach ($packageResourceFile in $packageResourceFiles) {
    $destinationPath = Join-Path `
        $PackageProjectDirectory `
        $packageResourceFile.RelativePath
    if ($PSCmdlet.ShouldProcess(
            $destinationPath,
            "Copy package resource from '$($packageResourceFile.SourcePath)'")) {
        $destinationDirectory = Split-Path -Parent $destinationPath
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        Copy-Item `
            -LiteralPath $packageResourceFile.SourcePath `
            -Destination $destinationPath
    }
}

$sourceStoreAssociationPath = Join-Path $appProjectDirectory 'Package.StoreAssociation.xml'
if (Test-Path -LiteralPath $sourceStoreAssociationPath -PathType Leaf) {
    if ($PSCmdlet.ShouldProcess(
            $packageStoreAssociationPath,
            "Copy Store association from '$sourceStoreAssociationPath'")) {
        Copy-Item `
            -LiteralPath $sourceStoreAssociationPath `
            -Destination $packageStoreAssociationPath
    }
}

$directoryBuildPropsPath = Join-Path $RepositoryRoot 'Directory.Build.props'
$hasDirectoryBuildImport = $false
if (Test-Path -LiteralPath $directoryBuildPropsPath -PathType Leaf) {
    $directoryBuildPropsText = Get-Content -LiteralPath $directoryBuildPropsPath -Raw
    $hasDirectoryBuildImport = $directoryBuildPropsText.Contains('CmdPal.Extension.props')
}

[pscustomobject]@{
    RepositoryRoot            = $RepositoryRoot
    AppProjectPath            = $AppProjectPath
    ExtensionPropsPath        = $ExtensionPropsPath
    PackageProjectPath        = $packageProjectPath
    PackageManifestPath       = $packageManifestPath
    PackageExecutable         = $packageExecutable
    SupportsNativeAot         = $SupportsNativeAot.IsPresent
    DirectoryBuildImportFound = $hasDirectoryBuildImport
    PreviewOnly               = [bool] $WhatIfPreference
}

if (-not $hasDirectoryBuildImport) {
    Write-Warning @"
Directory.Build.props does not import eng\CmdPal.Extension.props. Add the import
shown in docs\dev\WapPackaging.md before replacing application properties with
CmdPal* values.
"@
}
