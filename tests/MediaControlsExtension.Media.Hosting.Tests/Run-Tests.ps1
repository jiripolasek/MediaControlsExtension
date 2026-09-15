[CmdletBinding()]
param(
    [switch]$NativeAot,
    [switch]$SkipBuild,
    [switch]$ReadGsmtc,
    [ValidateSet('x64', 'ARM64')][string]$Architecture = 'x64',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$taskRepo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$flavor = if ($NativeAot) { 'native' } else { 'managed' }
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $taskRepo "artifacts\MediaBackendHost\$flavor-$Architecture" }
$results = Join-Path $taskRepo "artifacts\MediaBackendHost\$flavor-tests-$Architecture"
$project = Join-Path $PSScriptRoot 'JPSoftworks.MediaControlsExtension.Media.Hosting.Tests.csproj'
$exe = Join-Path $output 'JPSoftworks.MediaControlsExtension.Media.Hosting.Tests.exe'

if (!$SkipBuild) {
    $aot = $NativeAot.IsPresent.ToString().ToLowerInvariant()
    & dotnet publish $project -c Release -r "win-$($Architecture.ToLowerInvariant())" "-p:Platform=$Architecture" "-p:PublishAot=$aot" -p:HostingTestsWindowless=true -o $output --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Hosting publish failed: $LASTEXITCODE" }
}

[void][System.IO.Directory]::CreateDirectory($results)
$process = Start-Process -FilePath $exe -ArgumentList @('--self-test', ('"{0}"' -f $results)) -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput (Join-Path $results 'stdout.txt') -RedirectStandardError (Join-Path $results 'stderr.txt')
Get-Content -LiteralPath (Join-Path $results 'stdout.txt')
if ($process.ExitCode -ne 0) {
    Get-Content -LiteralPath (Join-Path $results 'stderr.txt')
    throw "Hosting checks failed: $($process.ExitCode)"
}

if ($ReadGsmtc) {
    $report = Join-Path $results 'gsmtc.txt'
    $process = Start-Process -FilePath $exe -ArgumentList @('--gsmtc', ('"{0}"' -f $report)) -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput (Join-Path $results 'gsmtc-stdout.txt') -RedirectStandardError (Join-Path $results 'gsmtc-stderr.txt')
    if ($process.ExitCode -ne 0) {
        Get-Content -LiteralPath (Join-Path $results 'gsmtc-stderr.txt')
        throw "GSMTC inspection failed: $($process.ExitCode)"
    }

    Get-Content -LiteralPath $report
}
