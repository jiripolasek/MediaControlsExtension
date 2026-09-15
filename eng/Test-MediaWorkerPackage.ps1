[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateSet('x64', 'ARM64')][string]$Platform = 'x64',
    [switch]$NativeAot,
    [switch]$RestartHost,
    [switch]$PayloadOnly,
    [ValidateSet('All', 'Lifetime', 'Commands')][string]$Checks = 'All',
    [string]$PackageFile
)

$ErrorActionPreference = 'Stop'
$taskRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskLayout = Join-Path $taskRepo "src\MediaControlsExtension.Package\bin\$Platform\$Configuration\AppX"
$taskOwnerPath = Join-Path $taskLayout 'JPSoftworks.MediaControlsExtension\JPSoftworks.MediaControlsExtension.exe'
$taskWorkerPath = Join-Path $taskLayout 'JPSoftworks.MediaControlsExtension\MediaHost\JPSoftworks.MediaControlsExtension.MediaHost.exe'
$taskWorkerDirectory = Split-Path -Parent $taskWorkerPath
$taskFlavor = if ($NativeAot) { 'native' } else { 'managed' }
$taskResults = Join-Path $taskRepo "artifacts\MediaBackendHost\production-$taskFlavor-$Platform"
[IO.Directory]::CreateDirectory($taskResults) | Out-Null
$taskEvidence = [Collections.Generic.List[string]]::new()
$taskArchive = $null
if ($PackageFile) {
    if (!$PayloadOnly) { throw '-PackageFile requires -PayloadOnly.' }
    $taskArchive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($PackageFile))
}

function Read-TestPayload([string]$Path) {
    if (!$taskArchive) { return ,([IO.File]::ReadAllBytes($Path)) }
    $taskEntry = $taskArchive.GetEntry($Path.Substring($taskLayout.Length + 1).Replace('\', '/'))
    if (!$taskEntry) { throw "Missing package entry: $Path" }
    $taskStream = $taskEntry.Open()
    $taskBuffer = [IO.MemoryStream]::new()
    try {
        $taskStream.CopyTo($taskBuffer)
        return ,($taskBuffer.ToArray())
    }
    finally { $taskStream.Dispose(); $taskBuffer.Dispose() }
}

function Test-TestPayload([string]$Path) {
    if (!$taskArchive) { return Test-Path -LiteralPath $Path }
    return $null -ne $taskArchive.GetEntry($Path.Substring($taskLayout.Length + 1).Replace('\', '/'))
}

foreach ($taskImage in @($taskOwnerPath, $taskWorkerPath)) {
    $taskBytes = Read-TestPayload $taskImage
    $taskMachine = [BitConverter]::ToUInt16($taskBytes, [BitConverter]::ToInt32($taskBytes, 0x3c) + 4)
    $taskExpected = if ($Platform -eq 'x64') { 0x8664 } else { 0xaa64 }
    if ($taskMachine -ne $taskExpected) { throw "Wrong image architecture: $taskImage" }
}
$taskManaged = Test-TestPayload (Join-Path $taskWorkerDirectory 'JPSoftworks.MediaControlsExtension.MediaHost.runtimeconfig.json')
if ($taskManaged -eq $NativeAot.IsPresent) { throw 'The worker payload does not match the requested managed/NativeAOT mode.' }
if ($taskManaged -and !(Test-TestPayload (Join-Path $taskWorkerDirectory 'coreclr.dll'))) { throw 'The worker is missing its self-contained runtime.' }
$taskPayloadLocation = if ($PackageFile) { $PackageFile } else { $taskLayout }
$taskEvidence.Add("PASS $Platform $taskFlavor owner and worker payload: $taskPayloadLocation")
if ($taskArchive) { $taskArchive.Dispose() }
if ($PayloadOnly) {
    $taskEvidence | Set-Content -LiteralPath (Join-Path $taskResults 'payload.txt')
    $taskEvidence
    return
}

function Read-TestSettings {
    try { $taskJson = [IO.File]::ReadAllText($taskSettingsPath) }
    catch [IO.FileNotFoundException] { return @{} }
    catch [IO.DirectoryNotFoundException] { return @{} }
    return $taskJson | ConvertFrom-Json -AsHashtable
}

function Restore-TestSettings {
    if (!$taskSettingsExisted) {
        if (Test-Path -LiteralPath $taskSettingsPath -PathType Leaf) { Remove-Item -LiteralPath $taskSettingsPath }
        return
    }
    $taskRestore = Read-TestSettings
    foreach ($taskKey in $taskKeys) {
        if ($taskPrevious[$taskKey].Present) { $taskRestore[$taskKey] = $taskPrevious[$taskKey].Value }
        else { $taskRestore.Remove($taskKey) }
    }
    $taskRestore | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath ($taskSettingsPath + '.worker-test.tmp') -Encoding utf8
    Move-Item -LiteralPath ($taskSettingsPath + '.worker-test.tmp') -Destination $taskSettingsPath -Force
}

$taskPackage = Get-AppxPackage -Name JiriPolasek.MediaControlsForCmdPal | Where-Object InstallLocation -eq $taskLayout
if (!$taskPackage) { throw 'Deploy this production package before running its lifetime checks.' }
$taskSettingsPath = Join-Path $env:LOCALAPPDATA "Packages\$($taskPackage.PackageFamilyName)\LocalState\settings.json"
$taskSettingsExisted = Test-Path -LiteralPath $taskSettingsPath -PathType Leaf
$taskOriginal = Read-TestSettings
$taskKeys = @('jpsoftworks.mediacontrols.MediaBackends.gsmtc.Enabled', 'jpsoftworks.mediacontrols.MediaBackends.gsmtc.worker.Enabled', 'jpsoftworks.mediacontrols.MediaBackends.dummy.worker.Enabled')
$taskPrevious = @{}
foreach ($taskKey in $taskKeys) {
    $taskPrevious[$taskKey] = @{ Present = $taskOriginal.ContainsKey($taskKey); Value = $taskOriginal[$taskKey] }
}

function Refresh-TestHost {
    if ($RestartHost) {
        $taskHosts = @(Get-Process -Name Microsoft.CmdPal.UI -ErrorAction Stop)
        if ($taskHosts.Count -ne 1) { throw 'Expected one running CmdPal host for restart validation.' }
        $taskHostPath = $taskHosts[0].Path
        $taskHosts[0] | Stop-Process -Force
        $taskHosts[0].WaitForExit()
        Start-Process -FilePath $taskHostPath -WorkingDirectory (Split-Path -Parent $taskHostPath) -WindowStyle Hidden | Out-Null
    }
    else {
        $taskHostSettings = Get-Content -LiteralPath (Join-Path $env:LOCALAPPDATA 'Microsoft.CmdPal\settings.json') -Raw | ConvertFrom-Json -AsHashtable
        if (!$taskHostSettings['AllowExternalReload']) { throw 'CmdPal external reload is disabled. Use -RestartHost, or run the reload check interactively.' }
        Start-Process -FilePath 'x-cmdpal://reload' -WindowStyle Hidden | Out-Null
    }
}

function Set-TestMode([bool]$UseWorker) {
    $taskSettings = Read-TestSettings
    $taskSettings[$taskKeys[0]] = (!$UseWorker).ToString().ToLowerInvariant()
    $taskSettings[$taskKeys[1]] = $UseWorker.ToString().ToLowerInvariant()
    $taskSettings[$taskKeys[2]] = 'false'
    [IO.Directory]::CreateDirectory((Split-Path -Parent $taskSettingsPath)) | Out-Null
    $taskSettings | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath ($taskSettingsPath + '.worker-test.tmp') -Encoding utf8
    Move-Item -LiteralPath ($taskSettingsPath + '.worker-test.tmp') -Destination $taskSettingsPath -Force
    if ($RestartHost) {
        Get-CimInstance Win32_Process -Filter "Name='JPSoftworks.MediaControlsExtension.exe'" |
            Where-Object ExecutablePath -eq $taskOwnerPath | ForEach-Object {
                Stop-Process -Id $_.ProcessId -Force
                Wait-TestExit $_.ProcessId
            }
    }
    Refresh-TestHost
}

function Get-TestWorkers {
    @(Get-CimInstance Win32_Process -Filter "Name='JPSoftworks.MediaControlsExtension.MediaHost.exe'" |
        Where-Object ExecutablePath -eq $taskWorkerPath)
}

function Wait-TestWorker([int]$PreviousId = 0) {
    $taskDeadline = [DateTime]::UtcNow.AddSeconds(120)
    do {
        $taskWorkers = @(Get-TestWorkers | Where-Object ProcessId -ne $PreviousId)
        if ($taskWorkers.Count -eq 1) {
            $taskParent = Get-CimInstance Win32_Process -Filter "ProcessId=$($taskWorkers[0].ParentProcessId)"
            if ($taskParent.ExecutablePath -eq $taskOwnerPath) { return $taskWorkers[0] }
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $taskDeadline)
    throw 'CmdPal did not activate exactly one worker owned by this packaged extension.'
}

function Wait-TestExit([int]$ProcessId) {
    $taskDeadline = [DateTime]::UtcNow.AddSeconds(12)
    while (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
        if ([DateTime]::UtcNow -ge $taskDeadline) { throw "Process $ProcessId survived its lifetime boundary." }
        Start-Sleep -Milliseconds 100
    }
}

function Test-LiveCommands {
    if (!$RestartHost) { throw 'Live command checks require -RestartHost to isolate the COM client.' }
    $taskClientOutput = Join-Path $taskRepo "artifacts\MediaBackendHost\package-commands-$Platform"
    & dotnet publish (Join-Path $taskRepo 'tests\MediaControlsExtension.Package.Tests\JPSoftworks.MediaControlsExtension.Package.Tests.csproj') -c Release -r "win-$($Platform.ToLowerInvariant())" "-p:Platform=$Platform" -p:PublishAot=false -o $taskClientOutput --nologo -v:minimal *> (Join-Path $taskResults 'commands-build.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Building the packaged command client failed; see commands-build.txt.' }
    Set-TestMode $false
    $taskHost = @(Get-Process -Name Microsoft.CmdPal.UI -ErrorAction Stop)
    if ($taskHost.Count -ne 1) { throw 'Expected one CmdPal host before isolating the COM client.' }
    $taskHostPath = $taskHost[0].Path
    $taskHost[0] | Stop-Process -Force
    $taskHost[0].WaitForExit()
    try {
        Get-CimInstance Win32_Process -Filter "Name='JPSoftworks.MediaControlsExtension.exe'" |
            Where-Object ExecutablePath -eq $taskOwnerPath | ForEach-Object {
                Stop-Process -Id $_.ProcessId -Force
                Wait-TestExit $_.ProcessId
            }
        $taskClient = Start-Process -FilePath (Join-Path $taskClientOutput 'JPSoftworks.MediaControlsExtension.Package.Tests.exe') -ArgumentList @(('"{0}"' -f $taskResults), ('"{0}"' -f $taskOwnerPath), ('"{0}"' -f $taskWorkerPath)) -WindowStyle Hidden -PassThru
        if (!$taskClient.WaitForExit(120000)) {
            $taskClient | Stop-Process -Force
            throw 'The packaged command client exceeded its deadline.'
        }
        $taskCommands = @(Get-Content -LiteralPath (Join-Path $taskResults 'commands.txt'))
        $taskCommands
        if ($taskClient.ExitCode -ne 0) { throw 'Packaged live command checks failed.' }
        $taskEvidence.AddRange([string[]]$taskCommands)
    }
    finally {
        Start-Process -FilePath $taskHostPath -WorkingDirectory (Split-Path -Parent $taskHostPath) -WindowStyle Hidden | Out-Null
    }
}

try {
    if ($Checks -ne 'Lifetime') {
        Test-LiveCommands
        if ($Checks -eq 'Commands') { return }
    }
    Set-TestMode $true
    $taskFirst = Wait-TestWorker
    $taskEvidence.Add("PASS CmdPal worker activation: owner $($taskFirst.ParentProcessId), worker $($taskFirst.ProcessId)")
    $taskProcess = Get-Process -Id $taskFirst.ProcessId
    $taskLoadsClr = @($taskProcess.Modules | Where-Object ModuleName -eq coreclr.dll).Count -ne 0
    if ($taskLoadsClr -eq $NativeAot.IsPresent) { throw 'The running worker does not match the requested execution mode.' }
    Stop-Process -Id $taskFirst.ProcessId -Force
    $taskRecovered = Wait-TestWorker $taskFirst.ProcessId
    if ($taskRecovered.ParentProcessId -ne $taskFirst.ParentProcessId) { throw 'The extension did not survive its worker crash.' }
    $taskEvidence.Add("PASS worker-only crash recovery: replacement $($taskRecovered.ProcessId), owner survived")
    Refresh-TestHost
    if ($RestartHost) {
        $taskReloaded = Wait-TestWorker
        $taskEvidence.Add("PASS CmdPal restart: worker $($taskReloaded.ProcessId) remains tied to live extension owner $($taskReloaded.ParentProcessId)")
    }
    else {
        Wait-TestExit $taskRecovered.ProcessId
        $taskReloaded = Wait-TestWorker $taskRecovered.ProcessId
        $taskEvidence.Add("PASS CmdPal reload exited old worker; owner $($taskReloaded.ParentProcessId), worker $($taskReloaded.ProcessId)")
    }
    Stop-Process -Id $taskReloaded.ParentProcessId -Force
    Wait-TestExit $taskReloaded.ParentProcessId
    Wait-TestExit $taskReloaded.ProcessId
    $taskEvidence.Add('PASS owner-only process termination left no worker')
    Set-TestMode $false
    $taskDeadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        $taskInternal = Get-CimInstance Win32_Process -Filter "Name='JPSoftworks.MediaControlsExtension.exe'" | Where-Object ExecutablePath -eq $taskOwnerPath
        if ($taskInternal -and @(Get-TestWorkers).Count -eq 0) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $taskDeadline)
    if (!$taskInternal -or @(Get-TestWorkers).Count -ne 0) { throw 'Switching back to internal GSMTC did not remove the worker.' }
    $taskEvidence.Add('PASS switching back to internal GSMTC leaves the extension alive without a worker')
}
finally {
    Restore-TestSettings
    if ($RestartHost) {
        Get-CimInstance Win32_Process -Filter "Name='JPSoftworks.MediaControlsExtension.exe'" |
            Where-Object ExecutablePath -eq $taskOwnerPath | ForEach-Object {
                Stop-Process -Id $_.ProcessId -Force
                Wait-TestExit $_.ProcessId
            }
    }
    Refresh-TestHost
    if ($Checks -ne 'Commands') { $taskEvidence | Set-Content -LiteralPath (Join-Path $taskResults 'lifetime.txt') }
}
$taskEvidence
