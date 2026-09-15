[CmdletBinding()]
param(
    [int]$DurationMinutes = 120,
    [int]$LifetimeCycles = 100,
    [ValidateRange(1, 60)][int]$SampleIntervalSeconds = 15,
    [switch]$NativeAot,
    [switch]$SkipBuild,
    [switch]$Packaged,
    [string]$WorkerPath,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
if ($DurationMinutes -lt 0 -or $LifetimeCycles -lt 1) { throw 'Use a nonnegative duration and at least one lifetime cycle.' }
$taskRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$taskFlavor = if ($NativeAot) { 'native' } else { 'managed' }
$taskOutput = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $taskRepo "artifacts\MediaBackendHost\$taskFlavor-x64" }
$taskResults = Join-Path $taskRepo ("artifacts\MediaBackendHost\soak-$taskFlavor-" + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($taskResults) | Out-Null
if (!$WorkerPath) {
    $WorkerPath = Join-Path $taskRepo 'src\MediaControlsExtension.Package\bin\x64\Release\AppX\JPSoftworks.MediaControlsExtension\MediaHost\JPSoftworks.MediaControlsExtension.MediaHost.exe'
}
$WorkerPath = [IO.Path]::GetFullPath($WorkerPath)
if (!$SkipBuild) {
    $taskProject = Join-Path $PSScriptRoot 'JPSoftworks.MediaControlsExtension.Media.Hosting.Tests.csproj'
    & dotnet publish $taskProject -c Release -r win-x64 -p:Platform=x64 "-p:PublishAot=$($NativeAot.IsPresent.ToString().ToLowerInvariant())" -p:HostingTestsWindowless=true -o $taskOutput --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Native fixture publish failed: $LASTEXITCODE" }
}
$taskExe = Join-Path $taskOutput 'JPSoftworks.MediaControlsExtension.Media.Hosting.Tests.exe'
$taskArguments = '--native-check "{0}" "{1}" {2} {3} {4}' -f $taskResults, $WorkerPath, $DurationMinutes, $LifetimeCycles, $SampleIntervalSeconds
Write-Output "Native check results: $taskResults"
if ($Packaged) {
    $taskPackage = Get-AppxPackage -Name JiriPolasek.MediaControlsForCmdPal | Where-Object { $WorkerPath.StartsWith($_.InstallLocation + '\', [StringComparison]::OrdinalIgnoreCase) }
    if (!$taskPackage) { throw 'The worker must belong to the registered production package.' }
    Invoke-CommandInDesktopPackage -PackageFamilyName $taskPackage.PackageFamilyName -AppId App -Command $taskExe -Args $taskArguments -PreventBreakaway -ErrorAction Stop
    Write-Output 'Launched under the production package identity. Check native-result.txt and native-resources.csv.'
}
else {
    $taskRun = Start-Process -FilePath $taskExe -ArgumentList $taskArguments -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput (Join-Path $taskResults 'stdout.txt') -RedirectStandardError (Join-Path $taskResults 'stderr.txt')
    Get-Content -LiteralPath (Join-Path $taskResults 'stdout.txt')
    if ($taskRun.ExitCode -ne 0) {
        Get-Content -LiteralPath (Join-Path $taskResults 'stderr.txt')
        throw "Native acceptance failed: $($taskRun.ExitCode)"
    }
    Get-Content -LiteralPath (Join-Path $taskResults 'native-result.txt')
}
