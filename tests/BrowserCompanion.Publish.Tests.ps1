<#
.SYNOPSIS
Checks companion payload replacement when switching between managed and AOT publishes.

.DESCRIPTION
Runs the production MSBuild copy and cleanup targets with timestamped payloads.
Requires the .NET 10 SDK; no compilation or package registration is performed.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $repositoryRoot "artifacts\BrowserCompanion.Publish.Tests-$PID-$([Guid]::NewGuid().ToString('N'))"
$publishDirectory = Join-Path $testRoot 'publish'
$companionDirectory = Join-Path $publishDirectory 'BrowserCompanion'
$companionName = 'JPSoftworks.MediaControlsExtension.BrowserCompanion'
$fixtureProject = Join-Path $testRoot 'CompanionPublish.proj'

function Write-FixtureFile {
    param([string] $Path, [string] $Content, [datetime] $Timestamp)
    [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Content)
    [IO.File]::SetLastWriteTimeUtc($Path, $Timestamp)
}

function Invoke-CompanionPublish {
    param([string] $PayloadDirectory, [bool] $Aot)
    $output = & dotnet msbuild $fixtureProject /nologo /v:minimal /t:CheckCompanionPublish `
        "/p:CompanionTargets=$(Join-Path $repositoryRoot 'eng\BrowserCompanion.targets')" `
        "/p:FixturePayload=$PayloadDirectory" "/p:PublishDir=$publishDirectory\" `
        "/p:PublishAot=$($Aot.ToString().ToLowerInvariant())" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Companion publish failed:`n$($output -join [Environment]::NewLine)"
    }

    $expectedFiles = @(Get-ChildItem -LiteralPath $PayloadDirectory -File)
    $actualFiles = @(Get-ChildItem -LiteralPath $companionDirectory -File)
    if ($actualFiles.Count -ne $expectedFiles.Count) {
        throw 'The published companion contains missing or obsolete files.'
    }
    foreach ($expectedFile in $expectedFiles) {
        $actualPath = Join-Path $companionDirectory $expectedFile.Name
        if (-not (Test-Path -LiteralPath $actualPath -PathType Leaf) -or
            [IO.File]::ReadAllText($actualPath) -cne [IO.File]::ReadAllText($expectedFile.FullName)) {
            throw "The published companion did not replace '$($expectedFile.Name)' with the selected payload."
        }
    }
    if ([IO.File]::ReadAllText((Join-Path $publishDirectory 'owner.txt')) -cne 'owner') {
        throw 'Companion cleanup changed an owner file.'
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
  <Import Project="$(CompanionTargets)" />
  <!-- Supply cached payloads while retaining the production copy and cleanup targets. -->
  <Target Name="PublishBrowserCompanion">
    <ItemGroup>
      <_FixtureFile Include="$(FixturePayload)\*" />
      <_BrowserCompanionPayload Include="@(_FixtureFile)">
        <RelativePath>%(_FixtureFile.Filename)%(_FixtureFile.Extension)</RelativePath>
      </_BrowserCompanionPayload>
    </ItemGroup>
  </Target>
  <Target Name="CheckCompanionPublish"
    DependsOnTargets="AddBrowserCompanionPublishPayload;_CopyResolvedFilesToPublishPreserveNewest;_CopyResolvedFilesToPublishAlways;_CopyResolvedFilesToPublishIfDifferent;ValidatePublishedBrowserCompanion" />
</Project>
'@
    [IO.File]::WriteAllText($fixtureProject, $fixture)

    $aotPayload = Join-Path $testRoot 'aot'
    $managedPayload = Join-Path $testRoot 'managed'
    $older = [datetime]::UtcNow.AddHours(-2)
    $newer = $older.AddHours(1)
    Write-FixtureFile (Join-Path $aotPayload "$companionName.exe") 'native companion executable' $older
    Write-FixtureFile (Join-Path $aotPayload 'BrowserIntegration.ps1') 'native' $older
    Write-FixtureFile (Join-Path $managedPayload "$companionName.exe") 'managed apphost' $newer
    Write-FixtureFile (Join-Path $managedPayload "$companionName.dll") 'managed companion assembly' $newer
    Write-FixtureFile (Join-Path $managedPayload 'coreclr.dll') 'managed runtime' $newer
    Write-FixtureFile (Join-Path $managedPayload 'BrowserIntegration.ps1') 'clrjit' $newer
    Write-FixtureFile (Join-Path $publishDirectory 'owner.txt') 'owner' $older

    Write-FixtureFile (Join-Path $companionDirectory 'BrowserCompanion.exe') 'obsolete companion executable' $older
    Invoke-CompanionPublish $managedPayload $false
    Write-Output 'PASS: Initial managed payload removes the old executable name.'
    Invoke-CompanionPublish $aotPayload $true
    Write-Output 'PASS: Cached AOT replaces newer managed files and removes managed dependencies.'

    foreach ($file in Get-ChildItem -LiteralPath $companionDirectory -File) {
        [IO.File]::SetLastWriteTimeUtc($file.FullName, $newer.AddHours(1))
    }
    Invoke-CompanionPublish $managedPayload $false
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
