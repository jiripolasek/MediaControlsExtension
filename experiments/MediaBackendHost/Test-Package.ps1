[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$KeepRegistered
)

$ErrorActionPreference = 'Stop'
$taskRepo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$output = Join-Path $taskRepo 'artifacts\MediaBackendHost\native-x64'
$layout = Join-Path $taskRepo 'artifacts\MediaBackendHost\package-x64'
$results = Join-Path $taskRepo ('artifacts\MediaBackendHost\packaged-tests-' + [Guid]::NewGuid().ToString('N'))
$project = Join-Path $PSScriptRoot '..\..\tests\MediaControlsExtension.Media.Hosting.Tests\JPSoftworks.MediaControlsExtension.Media.Hosting.Tests.csproj'
$packageName = 'JPSoftworks.MediaBackendHostSpike'
$registered = $false

if (Get-AppxPackage -Name $packageName) {
    throw 'The spike package is already registered. Remove that experimental package before rerunning this check.'
}

if (!$SkipBuild) {
    & dotnet publish $project -c Release -r win-x64 -p:Platform=x64 -p:PublishAot=true -p:HostingTestsWindowless=true -o $output --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "NativeAOT publish failed: $LASTEXITCODE" }
}

[void][System.IO.Directory]::CreateDirectory($layout)
[void][System.IO.Directory]::CreateDirectory((Join-Path $layout 'Assets'))
[void][System.IO.Directory]::CreateDirectory($results)
Get-ChildItem -LiteralPath $output -File | Where-Object Extension -ne '.pdb' | Copy-Item -Destination $layout
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Package.appxmanifest') -Destination (Join-Path $layout 'AppxManifest.xml')
foreach ($name in @('StoreLogo', 'Square150x150Logo', 'Square44x44Logo')) {
    Copy-Item -LiteralPath (Join-Path $taskRepo "src\MediaControlsExtension.Package\Assets\$name.scale-100.png") -Destination (Join-Path $layout "Assets\$name.png")
}

try {
    Add-AppxPackage -Register (Join-Path $layout 'AppxManifest.xml') -ErrorAction Stop
    $registered = $true
    $package = Get-AppxPackage -Name $packageName
    if (!$package) { throw 'The isolated spike package was not registered.' }
    $exe = Join-Path $layout 'JPSoftworks.MediaControlsExtension.Media.Hosting.Tests.exe'
    Invoke-CommandInDesktopPackage -PackageFamilyName $package.PackageFamilyName -AppId App -Command $exe -Args ('--package-check "{0}"' -f $results) -ErrorAction Stop
    $report = Join-Path $results 'package-result.txt'
    $deadline = [DateTime]::UtcNow.AddMinutes(2)
    while (!(Test-Path -LiteralPath $report)) {
        if ([DateTime]::UtcNow -ge $deadline) { throw "Packaged check timed out. Results: $results" }
        Start-Sleep -Milliseconds 200
    }

    $status = Get-Content -LiteralPath $report -Raw
    Get-Content -LiteralPath (Join-Path $results 'results.txt')
    if ($status -ne 'PASS') { throw $status }
    Get-Content -LiteralPath (Join-Path $results 'gsmtc.txt')
    Write-Output "Packaged acceptance passed. Results: $results"
}
finally {
    if ($registered -and !$KeepRegistered) {
        Get-AppxPackage -Name $packageName | Remove-AppxPackage -ErrorAction Stop
    }
}
