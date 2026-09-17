<#
.SYNOPSIS
Checks worker payload replacement when switching between managed and AOT publishes.

.DESCRIPTION
Runs the production MSBuild copy and cleanup targets with timestamped payloads.
Requires the .NET 10 SDK; no compilation or package registration is performed.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $repositoryRoot "artifacts\MediaWorker.Publish.Tests-$PID-$([Guid]::NewGuid().ToString('N'))"
$publishDirectory = Join-Path $testRoot 'publish'
$workerDirectory = Join-Path $publishDirectory 'MediaHost'
$workerName = 'JPSoftworks.MediaControlsExtension.MediaHost'
$fixtureProject = Join-Path $testRoot 'WorkerPublish.proj'

function Write-FixtureFile {
    param([string] $Path, [string] $Content, [datetime] $Timestamp)
    [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Content)
    [IO.File]::SetLastWriteTimeUtc($Path, $Timestamp)
}

function Invoke-WorkerPublish {
    param([string] $PayloadDirectory, [bool] $Aot)
    $output = & dotnet msbuild $fixtureProject /nologo /v:minimal /t:CheckWorkerPublish `
        "/p:WorkerTargets=$(Join-Path $repositoryRoot 'eng\MediaWorker.targets')" `
        "/p:FixturePayload=$PayloadDirectory" "/p:PublishDir=$publishDirectory\" `
        "/p:PublishAot=$($Aot.ToString().ToLowerInvariant())" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Worker publish failed:`n$($output -join [Environment]::NewLine)"
    }

    $expectedFiles = @(Get-ChildItem -LiteralPath $PayloadDirectory -File)
    $actualFiles = @(Get-ChildItem -LiteralPath $workerDirectory -File)
    if ($actualFiles.Count -ne $expectedFiles.Count) {
        throw 'The published worker contains missing or obsolete files.'
    }
    foreach ($expectedFile in $expectedFiles) {
        $actualPath = Join-Path $workerDirectory $expectedFile.Name
        if (-not (Test-Path -LiteralPath $actualPath -PathType Leaf) -or
            [IO.File]::ReadAllText($actualPath) -cne [IO.File]::ReadAllText($expectedFile.FullName)) {
            throw "The published worker did not replace '$($expectedFile.Name)' with the selected payload."
        }
    }
    if ([IO.File]::ReadAllText((Join-Path $publishDirectory 'owner.txt')) -cne 'owner') {
        throw 'Worker cleanup changed an owner file.'
    }
}

try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $fixture = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDefaultItems>false</EnableDefaultItems>
  </PropertyGroup>
  <Import Project="$(WorkerTargets)" />
  <!-- Supply cached payloads while retaining the production copy and cleanup targets. -->
  <Target Name="PublishMediaWorker">
    <ItemGroup>
      <_FixtureFile Include="$(FixturePayload)\*" />
      <_MediaWorkerPayload Include="@(_FixtureFile)">
        <RelativePath>%(_FixtureFile.Filename)%(_FixtureFile.Extension)</RelativePath>
      </_MediaWorkerPayload>
    </ItemGroup>
  </Target>
  <Target Name="CheckWorkerPublish"
    DependsOnTargets="AddMediaWorkerPublishPayload;_CopyResolvedFilesToPublishPreserveNewest;_CopyResolvedFilesToPublishAlways;_CopyResolvedFilesToPublishIfDifferent;ValidatePublishedMediaWorker" />
</Project>
'@
    [IO.File]::WriteAllText($fixtureProject, $fixture)

    $aotPayload = Join-Path $testRoot 'aot'
    $managedPayload = Join-Path $testRoot 'managed'
    $older = [datetime]::UtcNow.AddHours(-2)
    $newer = $older.AddHours(1)
    Write-FixtureFile (Join-Path $aotPayload "$workerName.exe") 'native worker executable' $older
    Write-FixtureFile (Join-Path $aotPayload 'backend.config') 'native' $older
    Write-FixtureFile (Join-Path $managedPayload "$workerName.exe") 'managed apphost' $newer
    Write-FixtureFile (Join-Path $managedPayload "$workerName.dll") 'managed worker assembly' $newer
    Write-FixtureFile (Join-Path $managedPayload 'coreclr.dll') 'managed runtime' $newer
    Write-FixtureFile (Join-Path $managedPayload 'backend.config') 'clrjit' $newer
    Write-FixtureFile (Join-Path $publishDirectory 'owner.txt') 'owner' $older

    Invoke-WorkerPublish $managedPayload $false
    Write-Output 'PASS: Initial managed payload.'
    Invoke-WorkerPublish $aotPayload $true
    Write-Output 'PASS: Cached AOT replaces newer managed files and removes managed dependencies.'

    foreach ($file in Get-ChildItem -LiteralPath $workerDirectory -File) {
        [IO.File]::SetLastWriteTimeUtc($file.FullName, $newer.AddHours(1))
    }
    Invoke-WorkerPublish $managedPayload $false
    Write-Output 'PASS: Cached managed payload replaces newer AOT files and restores dependencies.'
}
finally {
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot).TrimEnd('\')
    $artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')).TrimEnd('\')
    if (-not $resolvedRoot.StartsWith($artifactsRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove test files outside '$artifactsRoot'."
    }
    if (Test-Path -LiteralPath $resolvedRoot) {
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}
