<#
.SYNOPSIS
Exercises Deploy-Package.ps1 with fake build tools and package registration.

.DESCRIPTION
Runs the real deployment script against temporary files. No installed packages
are changed and no build tools or additional test modules are required.

.EXAMPLE
.\tests\Deploy-Package.Tests.ps1
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$deployScript = Join-Path $repositoryRoot 'eng\Deploy-Package.ps1'
$testRoot = Join-Path $repositoryRoot "artifacts\Deploy-Package.Tests-$PID-$([Guid]::NewGuid().ToString('N'))"
$originalProgramFiles = ${env:ProgramFiles(x86)}
$originalLocation = Get-Location
$lastExitCodeVariable = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
$originalLastExitCode = if ($lastExitCodeVariable) { $lastExitCodeVariable.Value }
$testCount = 0

function Assert-True {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) {
        throw $Message
    }
}

function Write-FixtureFile {
    param([string] $Path, [string] $Content = '')
    [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Content)
}

function Remove-FixtureDirectory {
    param([string] $Path)
    $resolvedPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot).TrimEnd('\')
    if ($resolvedPath -ne $resolvedRoot -and
        -not $resolvedPath.StartsWith($resolvedRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove test files outside '$resolvedRoot'."
    }
    if (Test-Path -LiteralPath $resolvedPath) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
}

function New-FixtureManifest {
    param([string] $Version, [string] $Publisher = 'CN=DeployTests', [string] $Architecture = 'x64')
    return "<Package><Identity Name='DeployTests' Publisher='$Publisher' Version='$Version' ProcessorArchitecture='$Architecture' /></Package>"
}

function New-FixtureRegistration {
    param([string] $Path)
    [xml] $manifest = Get-Content -LiteralPath (Join-Path $Path 'AppxManifest.xml') -Raw
    return [pscustomobject]@{
        Name = 'DeployTests'
        Publisher = [string] $manifest.Package.Identity.Publisher
        PackageFullName = "DeployTests_$($manifest.Package.Identity.Version)_x64"
        InstallLocation = $Path
        Version = [string] $manifest.Package.Identity.Version
        Architecture = [string] $manifest.Package.Identity.ProcessorArchitecture
        IsDevelopmentMode = $true
    }
}

function Get-AppxPackage {
    [CmdletBinding()]
    param([string] $Name, [string] $Publisher)
    Assert-True ($Name -eq 'DeployTests' -and $Publisher -eq 'CN=DeployTests') 'Package lookup must include name and publisher.'
    if ($null -ne $state.Registration) {
        return $state.Registration
    }
}

function Remove-AppxPackage {
    [CmdletBinding()]
    param([string] $Package, [switch] $PreserveApplicationData)
    Assert-True $PreserveApplicationData.IsPresent 'Unregistration must preserve application data.'
    $state.RemoveCount++
    if ($state.RemoveFails) {
        throw 'Simulated unregistration failure.'
    }
    $state.Registration = $null
    if ($state.DropStagingAfterRemove) {
        Remove-FixtureDirectory $state.StagingPath
        $state.DropStagingAfterRemove = $false
    }
}

function Add-AppxPackage {
    [CmdletBinding()]
    param([string] $Register, [switch] $ForceApplicationShutdown)
    Assert-True $ForceApplicationShutdown.IsPresent 'Registration must shut down package applications.'
    $state.AddCount++
    $registration = New-FixtureRegistration (Split-Path -Parent $Register)
    if ($registration.Version -eq '2.0.0.0') {
        if ($state.LockNewLayout) {
            $state.OpenFile = [IO.File]::Open((Join-Path $registration.InstallLocation 'app\app.exe'), 'Open', 'Read', 'Read')
        }
        if ($state.PartialRegistration) {
            $state.Registration = $registration
        }
        if ($state.AddFails) {
            throw 'Simulated registration failure.'
        }
        if ($state.BadRegisteredVersion) {
            $registration.Version = '9.0.0.0'
        }
    }
    elseif ($state.RollbackAddFails) {
        throw 'Simulated recovery failure.'
    }
    $state.Registration = $registration
}

function Start-Process {
    [CmdletBinding()]
    param([string] $FilePath)
    Assert-True ($FilePath -eq 'deploy-tests://reload') 'Only the configured test host URI may be invoked.'
    $state.ReloadRequested = $true
}

function Get-Process {
    [CmdletBinding()]
    param([string] $Name)
    Assert-True ($Name -eq 'DeployTestsHost') 'Only the configured test host may be queried.'
    return [pscustomobject]@{ Id = 1234 }
}

function Invoke-DeploymentCase {
    param(
        [string] $Name,
        [scriptblock] $Arrange = {},
        [scriptblock] $Verify = {},
        [string] $ExpectedError,
        [switch] $Aot,
        [switch] $IncludePrerelease,
        [switch] $ViaTestPackage
    )

    $caseRoot = Join-Path $testRoot $Name
    $outputPath = Join-Path $caseRoot 'package\bin\x64\Release'
    $deploymentPath = Join-Path $outputPath 'AppX'
    $state = @{
        CaseRoot = $caseRoot
        OutputPath = $outputPath
        DeploymentPath = $deploymentPath
        PackagePath = Join-Path $caseRoot 'artifacts\current_x64.msix'
        StagingPath = $null
        BackupPath = $null
        Registration = $null
        OpenFile = $null
        BuildCount = 0
        AddCount = 0
        RemoveCount = 0
        AddFails = $false
        RemoveFails = $false
        RollbackAddFails = $false
        DropStagingAfterRemove = $false
        PartialRegistration = $false
        BadRegisteredVersion = $false
        LockNewLayout = $false
        FailBackupMove = $false
        EmptyBuildResult = $false
        MissingPackage = $false
        BuildFails = $false
        UnpackFails = $false
        Publisher = 'CN=DeployTests'
        Architecture = 'x64'
        Managed = -not $Aot
        RelativeConfig = $false
        IncludePrereleaseRequested = $IncludePrerelease.IsPresent
        PrereleaseOnly = $false
        ExplicitVisualStudio = $false
        DiscoveryCount = 0
        ReloadRequested = $false
    }

    Write-FixtureFile (Join-Path $caseRoot 'app.csproj') '<Project />'
    Write-FixtureFile (Join-Path $caseRoot 'package\app.wapproj') '<Project />'
    Write-FixtureFile (Join-Path $caseRoot 'package\Package.appxmanifest') (New-FixtureManifest '2.0.0.0')
    Write-FixtureFile (Join-Path $deploymentPath 'AppxManifest.xml') (New-FixtureManifest '1.0.0.0')
    Write-FixtureFile (Join-Path $deploymentPath 'original.txt') 'Previous layout'
    Write-FixtureFile (Join-Path $outputPath 'AppX.staging-other\keep.txt') 'Another deployment'
    Write-FixtureFile (Join-Path $outputPath 'AppX.backup-other\keep.txt') 'A recovery backup'
    $state.Registration = New-FixtureRegistration $deploymentPath

    $configPath = Join-Path $caseRoot 'Package.config.psd1'
    Write-FixtureFile $configPath @'
@{
    RepositoryRoot = '.'
    AppProjectPath = 'app.csproj'
    WapProjectPath = 'package\app.wapproj'
    PackageManifestPath = 'package\Package.appxmanifest'
    ArtifactsPath = 'artifacts'
    PackageExecutablePath = 'app\app.exe'
    DefaultConfiguration = 'Release'
    DefaultPlatform = 'x64'
    RuntimeIdentifiers = @{ x64 = 'win-x64' }
    Host = @{
        ProcessName = 'DeployTestsHost'
        LaunchUri = 'deploy-tests://'
        ReloadUri = 'deploy-tests://reload'
    }
}
'@

    ${env:ProgramFiles(x86)} = Join-Path $caseRoot 'Program Files (x86)'
    $vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $vsPath = Join-Path $caseRoot 'Visual Studio'
    $msbuildPath = Join-Path $vsPath 'MSBuild\Current\Bin\MSBuild.exe'
    $makeAppxPath = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe'
    foreach ($path in @($vswherePath, $msbuildPath, $makeAppxPath,
        (Join-Path $vsPath 'MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets'),
        (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\x64\makeappx.exe'))) {
        Write-FixtureFile $path
    }

    # Full-path functions intercept native tools without changing the deployment script.
    Set-Item -LiteralPath "Function:$vswherePath" -Value {
        $state.DiscoveryCount++
        Assert-True ('-latest' -notin $args) 'Discovery must consider older installations with DesktopBridge.'
        Assert-True ('[18.0,)' -in $args) 'Automatic discovery must require Visual Studio 2026 or later.'
        Assert-True (('-prerelease' -in $args) -eq $state.IncludePrereleaseRequested) 'Prerelease discovery must require an explicit opt-in.'
        $global:LASTEXITCODE = 0
        if ($state.PrereleaseOnly -and '-prerelease' -notin $args) {
            return
        }
        Join-Path $caseRoot 'Newer Visual Studio without DesktopBridge'
        $vsPath
    }
    Set-Item -LiteralPath "Function:$msbuildPath" -Value {
        $state.BuildCount++
        $resultArgument = @($args | Where-Object { $_ -like '/getResultOutputFile:*' })
        Assert-True ($resultArgument.Count -eq 1) 'MSBuild must report its exact package output.'
        Assert-True ('/getProperty:AppxPackageOutput' -in $args) 'MSBuild must query AppxPackageOutput.'
        $resultPath = $resultArgument[0].Substring('/getResultOutputFile:'.Length)
        Write-FixtureFile $state.PackagePath 'Current build'
        Write-FixtureFile (Join-Path $caseRoot 'artifacts\unrelated_x64.msix') 'Newer unrelated package'
        $reportedPath = if ($state.EmptyBuildResult) { '' } elseif ($state.MissingPackage) { "$($state.PackagePath).missing.msix" } else { $state.PackagePath }
        Write-Output 'Build diagnostic output.'
        Write-FixtureFile $resultPath $reportedPath
        $global:LASTEXITCODE = if ($state.BuildFails) { 7 } else { 0 }
    }
    Set-Item -LiteralPath "Function:$makeAppxPath" -Value {
        $packageIndex = [Array]::IndexOf($args, '/p')
        Assert-True ($args[$packageIndex + 1] -eq $state.PackagePath) 'The current build must be used even when stale packages are newer.'
        $directoryIndex = [Array]::IndexOf($args, '/d')
        $state.StagingPath = $args[$directoryIndex + 1]
        $state.BackupPath = $state.StagingPath.Replace('AppX.staging-', 'AppX.backup-')
        Write-FixtureFile (Join-Path $state.StagingPath 'AppxManifest.xml') (New-FixtureManifest '2.0.0.0' $state.Publisher $state.Architecture)
        Write-FixtureFile (Join-Path $state.StagingPath 'app\app.exe') 'New executable'
        if ($state.Managed) {
            Write-FixtureFile (Join-Path $state.StagingPath 'app\coreclr.dll') 'Managed runtime'
        }
        if ($state.FailBackupMove) {
            Write-FixtureFile $state.BackupPath 'Existing file blocks the move'
        }
        $global:LASTEXITCODE = if ($state.UnpackFails) { 8 } else { 0 }
    }

    try {
        & $Arrange
        $parameters = @{ ConfigPath = $configPath; Aot = $Aot; IncludePrerelease = $IncludePrerelease }
        if ($state.ExplicitVisualStudio) {
            $parameters.VisualStudioPath = $vsPath
        }
        if ($state.RelativeConfig) {
            Set-Location -LiteralPath $caseRoot
            $parameters.ConfigPath = '.\Package.config.psd1'
        }
        $caughtError = $null
        try {
            $scriptToRun = if ($ViaTestPackage) { Join-Path $repositoryRoot 'eng\Test-Package.ps1' } else { $deployScript }
            & $scriptToRun @parameters 6>$null 3>$null | Out-Null
        }
        catch {
            $caughtError = $_
        }
        if ($ExpectedError) {
            Assert-True ($null -ne $caughtError -and $caughtError.ToString() -like "*$ExpectedError*") "Expected '$ExpectedError', got '$caughtError'."
        }
        elseif ($caughtError) {
            throw $caughtError
        }
        & $Verify
        Assert-True (Test-Path -LiteralPath (Join-Path $outputPath 'AppX.staging-other\keep.txt')) 'Another deployment staging directory was removed.'
        Assert-True (Test-Path -LiteralPath (Join-Path $outputPath 'AppX.backup-other\keep.txt')) 'Another deployment backup was removed.'
        if ($state.StagingPath) {
            Assert-True (-not (Test-Path -LiteralPath $state.StagingPath)) 'The current staging layout was not cleaned up.'
        }
        Assert-True (@(Get-ChildItem -LiteralPath $outputPath -Filter 'AppX.build-*.txt').Count -eq 0) 'Build result files were not cleaned up.'
        $script:testCount++
        Write-Host "PASS: $Name"
    }
    finally {
        if ($state.OpenFile) {
            $state.OpenFile.Dispose()
        }
        Set-Location -LiteralPath $originalLocation.Path
        foreach ($path in @($vswherePath, $msbuildPath, $makeAppxPath)) {
            Remove-Item -LiteralPath "Function:$path"
        }
    }
}

$verifyOriginal = {
    Assert-True (Test-Path -LiteralPath (Join-Path $state.DeploymentPath 'original.txt')) 'The previous layout was lost.'
    Assert-True ($state.Registration.Version -eq '1.0.0.0') 'The previous registration was not restored.'
}
$verifySuccess = {
    Assert-True ($state.Registration.Version -eq '2.0.0.0') 'The new package was not registered.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $state.DeploymentPath 'original.txt'))) 'The previous payload was retained in the new layout.'
    Assert-True (-not (Test-Path -LiteralPath $state.BackupPath)) 'The successful deployment backup was not removed.'
}

try {
    Invoke-DeploymentCase 'managed deployment' -Verify $verifySuccess
    Invoke-DeploymentCase 'AOT deployment' -Aot -Verify $verifySuccess
    Invoke-DeploymentCase 'prerelease requires opt-in' -Arrange { $state.PrereleaseOnly = $true } -ExpectedError 'Use -IncludePrerelease' -Verify {
        Assert-True ($state.BuildCount -eq 0) 'An excluded prerelease installation must not build.'
        & $verifyOriginal
    }
    Invoke-DeploymentCase 'prerelease opt-in' -IncludePrerelease -Arrange { $state.PrereleaseOnly = $true } -Verify $verifySuccess
    Invoke-DeploymentCase 'explicit prerelease path' -Arrange {
        $state.PrereleaseOnly = $true
        $state.ExplicitVisualStudio = $true
    } -Verify {
        Assert-True ($state.DiscoveryCount -eq 0) 'An explicit installation must bypass discovery.'
        & $verifySuccess
    }
    Invoke-DeploymentCase 'host wrapper prerelease opt-in' -IncludePrerelease -ViaTestPackage -Arrange { $state.PrereleaseOnly = $true } -Verify {
        Assert-True $state.ReloadRequested 'The host wrapper must deploy and request the configured reload.'
        & $verifySuccess
    }
    Invoke-DeploymentCase 'relative configuration' -Arrange { $state.RelativeConfig = $true } -Verify $verifySuccess
    Invoke-DeploymentCase 'first deployment' -Arrange {
        $state.Registration = $null
        Remove-FixtureDirectory $state.DeploymentPath
    } -Verify $verifySuccess
    Invoke-DeploymentCase 'failed backup move' -Arrange { $state.FailBackupMove = $true } -ExpectedError 'Move' -Verify $verifyOriginal
    Invoke-DeploymentCase 'failed staging move' -Arrange { $state.DropStagingAfterRemove = $true } -ExpectedError 'Move' -Verify $verifyOriginal
    Invoke-DeploymentCase 'failed registration' -Arrange { $state.AddFails = $true } -ExpectedError 'Simulated registration failure' -Verify $verifyOriginal
    Invoke-DeploymentCase 'partial registration' -Arrange {
        $state.AddFails = $true
        $state.PartialRegistration = $true
    } -ExpectedError 'Simulated registration failure' -Verify {
        & $verifyOriginal
        Assert-True ($state.RemoveCount -eq 2) 'The partial registration was not removed before rollback.'
    }
    Invoke-DeploymentCase 'failed verification' -Arrange { $state.BadRegisteredVersion = $true } -ExpectedError 'expected layout' -Verify $verifyOriginal
    Invoke-DeploymentCase 'failed unregistration' -Arrange { $state.RemoveFails = $true } -ExpectedError 'Simulated unregistration failure' -Verify $verifyOriginal
    Invoke-DeploymentCase 'failed recovery registration' -Arrange {
        $state.AddFails = $true
        $state.RollbackAddFails = $true
    } -ExpectedError 'Simulated registration failure' -Verify {
        Assert-True (Test-Path -LiteralPath (Join-Path $state.DeploymentPath 'original.txt')) 'Rollback must preserve the previous layout even if registration fails.'
    }
    Invoke-DeploymentCase 'failed recovery cleanup' -Arrange {
        $state.AddFails = $true
        $state.LockNewLayout = $true
    } -ExpectedError 'Simulated registration failure' -Verify {
        Assert-True (Test-Path -LiteralPath (Join-Path $state.BackupPath 'original.txt')) 'The backup must survive a failed rollback.'
    }
    Invoke-DeploymentCase 'wrong publisher' -Arrange { $state.Publisher = 'CN=OtherPublisher' } -ExpectedError 'publisher' -Verify $verifyOriginal
    Invoke-DeploymentCase 'wrong architecture' -Arrange { $state.Architecture = 'arm64' } -ExpectedError 'architecture' -Verify $verifyOriginal
    Invoke-DeploymentCase 'wrong publish mode' -Arrange { $state.Managed = $false } -ExpectedError 'no coreclr.dll' -Verify $verifyOriginal
    Invoke-DeploymentCase 'installed store package' -Arrange { $state.Registration.IsDevelopmentMode = $false } -ExpectedError 'not a development registration' -Verify {
        Assert-True ($state.RemoveCount -eq 0) 'A non-development package must not be unregistered.'
        & $verifyOriginal
    }
    Invoke-DeploymentCase 'empty build result' -Arrange { $state.EmptyBuildResult = $true } -ExpectedError 'did not report AppxPackageOutput' -Verify $verifyOriginal
    Invoke-DeploymentCase 'missing reported package' -Arrange { $state.MissingPackage = $true } -ExpectedError 'reported MSIX' -Verify $verifyOriginal
    Invoke-DeploymentCase 'failed build' -Arrange { $state.BuildFails = $true } -ExpectedError 'exit code 7' -Verify $verifyOriginal
    Invoke-DeploymentCase 'failed unpack' -Arrange { $state.UnpackFails = $true } -ExpectedError 'exit code 8' -Verify $verifyOriginal
    Invoke-DeploymentCase 'executable path escapes package' -Arrange {
        $config = [IO.File]::ReadAllText($configPath).Replace("'app\app.exe'", "'..\outside.exe'")
        Write-FixtureFile $configPath $config
    } -ExpectedError 'must stay within the package root' -Verify {
        Assert-True ($state.BuildCount -eq 0) 'Invalid configuration must fail before building.'
        & $verifyOriginal
    }
    Write-Host "$testCount deployment regression tests passed."
}
finally {
    ${env:ProgramFiles(x86)} = $originalProgramFiles
    if ($lastExitCodeVariable) {
        $global:LASTEXITCODE = $originalLastExitCode
    }
    else {
        Remove-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
    }
    Set-Location -LiteralPath $originalLocation.Path
    Remove-FixtureDirectory $testRoot
}
