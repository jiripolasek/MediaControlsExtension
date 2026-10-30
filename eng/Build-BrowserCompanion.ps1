<#
.SYNOPSIS
Builds a portable, self-contained browser companion release bundle.
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')][string] $Runtime = 'win-x64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repoRoot 'src\BrowserCompanion'
$manifest = Get-Content (Join-Path $sourceRoot 'extension\manifest.json') -Raw | ConvertFrom-Json
$name = "MediaControlsCompanion-$($manifest.version)-$Runtime"
$outputRoot = Join-Path $repoRoot "artifacts\BrowserCompanion\release\$([DateTime]::Now.ToString('yyyyMMdd-HHmmss'))-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$bundle = Join-Path $outputRoot $name
$hostOutput = Join-Path $outputRoot 'publish'
[IO.Directory]::CreateDirectory($bundle) | Out-Null

dotnet publish (Join-Path $sourceRoot 'host\JPSoftworks.MediaControlsExtension.BrowserCompanion.csproj') -c Release -r $Runtime --self-contained `
    -p:PublishAot=true -p:Version=$($manifest.version) -o $hostOutput
if ($LASTEXITCODE -ne 0) { throw 'Native host publish failed.' }

$hostBundle = Join-Path $bundle 'host'
[IO.Directory]::CreateDirectory($hostBundle) | Out-Null
Copy-Item -LiteralPath (Join-Path $hostOutput 'JPSoftworks.MediaControlsExtension.BrowserCompanion.exe') -Destination $hostBundle
$extensionBundle = Join-Path $bundle 'extension'
[IO.Directory]::CreateDirectory((Join-Path $extensionBundle 'icons')) | Out-Null
$extensionFiles = @('manifest.json', 'background.js', 'content.js', 'page.js', 'routing.mjs', 'grouping.mjs',
    'popup.html', 'popup.js', 'popup.css', 'icons\icon-16.png', 'icons\icon-32.png', 'icons\icon-48.png', 'icons\icon-128.png', 'icons\icon-256.png')
foreach ($file in $extensionFiles) {
    Copy-Item -LiteralPath (Join-Path $sourceRoot "extension\$file") -Destination (Join-Path $extensionBundle $file)
}
Copy-Item -LiteralPath (Join-Path $sourceRoot 'setup\Setup.ps1'), (Join-Path $sourceRoot 'setup\Registration.psm1') -Destination $bundle
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\user\browser-companion.md') -Destination (Join-Path $bundle 'README.md')
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\user\browser-companion-privacy.md') -Destination (Join-Path $bundle 'PRIVACY.md')
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE.txt') -Destination $bundle
$assets = Get-Content (Join-Path $sourceRoot 'host\obj\project.assets.json') -Raw | ConvertFrom-Json
$runtimePackage = @($assets.project.frameworks.'net10.0'.downloadDependencies | Where-Object { $_.name -eq "Microsoft.NETCore.App.Runtime.NativeAOT.$Runtime" })
if ($runtimePackage.Count -ne 1 -or $runtimePackage[0].version -notmatch '^\[([^,]+),\s*\1\]$') { throw 'Cannot identify the published native runtime notices.' }
$runtimeVersion = $Matches[1]
$runtimeDirectory = $null
foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
    $candidate = Join-Path $folder "microsoft.netcore.app.runtime.nativeaot.$Runtime/$runtimeVersion"
    if (Test-Path -LiteralPath $candidate -PathType Container) { $runtimeDirectory = $candidate; break }
}
if (-not $runtimeDirectory) { throw 'Native runtime package is missing.' }
$notices = Join-Path $bundle 'licenses\dotnet'
[IO.Directory]::CreateDirectory($notices) | Out-Null
Copy-Item -LiteralPath (Join-Path $runtimeDirectory 'LICENSE.TXT'), (Join-Path $runtimeDirectory 'THIRD-PARTY-NOTICES.TXT') -Destination $notices
$archive = Join-Path $outputRoot "$name.zip"
Compress-Archive -Path $bundle -DestinationPath $archive
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$archive.sha256", "$hash  $name.zip`r`n", [Text.UTF8Encoding]::new($false))
Write-Host "Release bundle: $archive"
Write-Output $archive
