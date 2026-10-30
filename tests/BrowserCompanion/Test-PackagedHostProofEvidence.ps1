[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $EvidenceDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$evidence = (Resolve-Path -LiteralPath $EvidenceDirectory).Path
$checks = [Collections.Generic.List[object]]::new()

function Read-Evidence([string] $Name) {
    Get-Content -LiteralPath (Join-Path $evidence $Name) -Raw | ConvertFrom-Json
}

function Assert-Evidence([bool] $Condition, [string] $Detail) {
    if (-not $Condition) { throw $Detail }
}

function Test-Requirement([string] $Name, [scriptblock] $Check) {
    & $Check
    $checks.Add([ordered]@{ name = $Name; passed = $true })
}

function Assert-Registration($Desktop, [string] $Manifest) {
    Assert-Evidence ($null -eq $Desktop.package) 'Registry evidence must come from an unpackaged process.'
    Assert-Evidence ($Desktop.keys.Count -eq 2) 'Expected both browser registration keys.'
    foreach ($browser in @('Microsoft\Edge', 'Google\Chrome')) {
        $path = "Software\$browser\NativeMessagingHosts\com.jpsoftworks.media_controls_lifecycle"
        $entries = @($Desktop.keys | Where-Object path -eq $path)
        Assert-Evidence ($entries.Count -eq 1 -and $entries[0].value -eq $Manifest) "Incorrect registration: $browser"
    }
}

function Get-BrowserConnection($Catalog, [string] $Browser) {
    $connections = @($Catalog.connections | Where-Object applicationId -eq $Browser)
    Assert-Evidence ($connections.Count -eq 1) "Expected one connection: $Browser"
    $connection = $connections[0]
    Assert-Evidence ($connection.hasSnapshot -and $connection.heartbeatAgeMs -ge 0 -and $connection.heartbeatAgeMs -lt 6500) "Stale or incomplete connection: $Browser"
    return $connection
}

$v1 = Read-Evidence 'signed-installed-v1.json'
$v2 = Read-Evidence 'signed-installed-v2.json'
$before = Read-Evidence 'signed-catalog-v1.json'
$after = Read-Evidence 'signed-catalog-v2.json'

Test-Requirement 'InstallRegistersBothBrowsers' {
    Assert-Evidence ($v1.PackageFullName -eq 'JPSoftworks.MediaControls.BridgeProof_0.1.0.0_x64__15b1mzc2p8w76' -and -not $v1.IsDevelopmentMode -and $v1.Status -eq 0) 'Expected a healthy signed v1 package.'
    Assert-Registration (Read-Evidence 'signed-desktop-v1.json') (Join-Path $v1.InstallLocation 'BrowserCompanion\native-host.json')
}

Test-Requirement 'BothBrowsersConnectToPackagedBridge' {
    foreach ($browser in @('MSEdgeDev', 'Chrome')) { $null = Get-BrowserConnection $before $browser }
    foreach ($session in @('bridge-lifecycle', 'bridge-lifecycle-chrome')) {
        $handshake = Get-Content -LiteralPath (Join-Path $evidence "signed-handshake-v1-$session.txt") -Raw
        Assert-Evidence ($handshake -match '"type":"ready"') "Native handshake failed: $session"
    }
    $processes = @(Read-Evidence 'signed-processes-v1.json')
    Assert-Evidence ($processes.Count -eq 3) 'Expected two bridges and one relay.'
    Assert-Evidence (@($processes | Where-Object ExecutablePath -ne (Join-Path $v1.InstallLocation 'BrowserCompanion\BrowserCompanion.exe')).Count -eq 0) 'A process did not run from installed v1.'
}

Test-Requirement 'UpgradeUpdatesRegistration' {
    Assert-Evidence ($v2.PackageFullName -eq 'JPSoftworks.MediaControls.BridgeProof_0.2.0.0_x64__15b1mzc2p8w76' -and -not $v2.IsDevelopmentMode -and $v2.Status -eq 0) 'Expected a healthy signed v2 package.'
    Assert-Registration (Read-Evidence 'signed-desktop-v2.json') (Join-Path $v2.InstallLocation 'BrowserCompanion\native-host.json')
}

Test-Requirement 'BrowsersReconnectToNewBridge' {
    foreach ($browser in @('MSEdgeDev', 'Chrome')) {
        $old = Get-BrowserConnection $before $browser
        $new = Get-BrowserConnection $after $browser
        Assert-Evidence ($old.connectionId -ne $new.connectionId) "Old connection survived upgrade: $browser"
    }
    $processes = @(Read-Evidence 'signed-processes-v2.json')
    Assert-Evidence ($processes.Count -eq 3) 'Expected two new bridges and one new relay.'
    Assert-Evidence (@($processes | Where-Object ExecutablePath -ne (Join-Path $v2.InstallLocation 'BrowserCompanion\BrowserCompanion.exe')).Count -eq 0) 'An old package process survived upgrade.'
    $upgrade = Read-Evidence 'signed-upgrade-check.json'
    Assert-Evidence ($upgrade.oldProcessesExited -and $upgrade.browsersReconnectedAutomatically) 'Automatic reconnection was not observed.'
}

Test-Requirement 'UninstallRemovesRegistrationAndProcesses' {
    $uninstall = Read-Evidence 'signed-uninstall-check.json'
    Assert-Evidence ($uninstall.remainingPackages -eq 0 -and $uninstall.remainingProcesses -eq 0) 'Package or processes remained after uninstall.'
    $desktop = Read-Evidence 'signed-desktop-uninstalled.json'
    Assert-Evidence ($null -eq $desktop.package -and $desktop.keys.Count -eq 2 -and @($desktop.keys | Where-Object { $null -ne $_.value }).Count -eq 0) 'Browser registration survived uninstall.'
    foreach ($session in @('bridge-lifecycle', 'bridge-lifecycle-chrome')) {
        $handshake = Get-Content -LiteralPath (Join-Path $evidence "signed-handshake-uninstalled-$session.txt") -Raw
        Assert-Evidence ($handshake -match '"error":"Specified native messaging host not found\."') "Unexpected post-uninstall handshake: $session"
    }
}

$cleanup = Read-Evidence 'final-cleanup-check.json'
Assert-Evidence ($cleanup.existingPackageUnchanged -and $cleanup.existingManifestUnchanged -and $cleanup.proofPackageAbsent -and $cleanup.proofProcessesAbsent -and $cleanup.signingCertificateRetained) 'Incomplete cleanup or existing installation changed.'
Assert-Evidence ($cleanup.temporaryTrust.Count -eq 2 -and @($cleanup.temporaryTrust | Where-Object present).Count -eq 0) 'Temporary certificate trust remains.'
[ordered]@{ passed = $true; checks = @($checks); cleanupPassed = $true; evidenceDirectory = $evidence } | ConvertTo-Json -Depth 5
