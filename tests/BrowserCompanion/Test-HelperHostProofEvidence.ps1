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
    Assert-Evidence ($null -eq $Desktop.package -and $Desktop.keys.Count -eq 2) 'Expected independent desktop registry evidence.'
    foreach ($browser in @('Microsoft\Edge', 'Google\Chrome')) {
        $path = "Software\$browser\NativeMessagingHosts\com.jpsoftworks.media_controls_lifecycle"
        $keys = @($Desktop.keys | Where-Object path -eq $path)
        Assert-Evidence ($keys.Count -eq 1 -and $keys[0].exists -and $keys[0].value -eq $Manifest) "Incorrect registration: $browser"
    }
}

function Get-BrowserConnection($Catalog, [string] $Browser) {
    $connections = @($Catalog.connections | Where-Object applicationId -eq $Browser)
    Assert-Evidence ($connections.Count -eq 1) "Expected one connection: $Browser"
    $connection = $connections[0]
    Assert-Evidence ($connection.hasSnapshot -and $connection.heartbeatAgeMs -ge 0 -and $connection.heartbeatAgeMs -lt 6500) "Stale or incomplete connection: $Browser"
    return $connection
}

function Assert-Processes([string] $File, [string] $PackageLocation) {
    $processes = @(Read-Evidence $File)
    Assert-Evidence ($processes.Count -eq 3) 'Expected two bridges and one relay.'
    Assert-Evidence (@($processes | Where-Object ExecutablePath -ne (Join-Path $PackageLocation 'BrowserCompanion\BrowserCompanion.exe')).Count -eq 0) 'Unexpected package executable path.'
}

$v1 = Read-Evidence 'installed-v1.json'
$v2 = Read-Evidence 'installed-v2.json'
$registration = Read-Evidence 'registration-v1-default.json'
$external = Read-Evidence 'external-files-v1.json'
$before = Read-Evidence 'catalog-v1.json'
$after = Read-Evidence 'catalog-v2-ready.json'
$upgrade = Read-Evidence 'upgrade-check.json'
$uninstall = Read-Evidence 'uninstall-check.json'

Test-Requirement 'InstallUsesOnlyRunFullTrust' {
    Assert-Evidence ($v1.PackageFullName -eq 'JPSoftworks.MediaControls.BridgeProof_0.1.0.0_x64__15b1mzc2p8w76' -and -not $v1.IsDevelopmentMode -and $v1.Status -eq 0) 'Expected signed v1 installation.'
    foreach ($version in @('0.1.0.0', '0.2.0.0')) {
        $layout = Join-Path $evidence $version
        [xml] $manifest = Get-Content -LiteralPath (Join-Path $layout 'AppxManifest.xml') -Raw
        $capabilities = @($manifest.Package.Capabilities.ChildNodes | Where-Object NodeType -eq Element | ForEach-Object { $_.GetAttribute('Name') })
        Assert-Evidence ($capabilities.Count -eq 1 -and $capabilities[0] -eq 'runFullTrust') 'Unexpected capability.'
        Assert-Evidence (@(Get-ChildItem -LiteralPath $layout -Filter '*.sccd').Count -eq 0) 'The helper proof must not use an SCCD.'
    }
    Assert-Evidence ($registration.success -and $registration.parentPackage -eq $v1.PackageFullName -and $registration.capabilities.Count -eq 1 -and $registration.capabilities[0] -eq 'runFullTrust') 'Expected a packaged parent with only runFullTrust.'
    Assert-Evidence ($registration.child.success -and $null -eq $registration.child.package) 'Expected unpackaged registration helper.'
    Assert-Registration (Read-Evidence 'desktop-v1.json') $external.manifest
    Assert-Evidence ((Read-Evidence 'alias-isolation.json').preservesEnvironment) 'Alias lost relay isolation.'
}

Test-Requirement 'BothBrowsersConnectThroughHelper' {
    foreach ($browser in @('MSEdgeDev', 'Chrome')) { $null = Get-BrowserConnection $before $browser }
    foreach ($browser in @('edge', 'chrome')) {
        $handshake = Get-Content -LiteralPath (Join-Path $evidence "handshake-v1-$browser.txt") -Raw
        Assert-Evidence ($handshake -match '"type":"ready"') "Native handshake failed: $browser"
    }
    Assert-Processes 'processes-v1.json' $v1.InstallLocation
}

Test-Requirement 'UpgradeKeepsRegistrationStable' {
    Assert-Evidence ($v2.PackageFullName -eq 'JPSoftworks.MediaControls.BridgeProof_0.2.0.0_x64__15b1mzc2p8w76' -and -not $v2.IsDevelopmentMode -and $v2.Status -eq 0) 'Expected signed v2 installation.'
    Assert-Registration (Read-Evidence 'desktop-v2.json') $external.manifest
    Assert-Evidence ($upgrade.registrationUnchanged -and $upgrade.manifestUnchanged -and -not $upgrade.helperRerunAfterUpgrade) 'Upgrade required registration repair.'
    Assert-Evidence ($uninstall.externalManifestSha256 -eq $external.manifestSha256) 'Stable manifest changed during the lifecycle.'
}

Test-Requirement 'BrowsersReconnectThroughNewAliasTarget' {
    foreach ($browser in @('MSEdgeDev', 'Chrome')) {
        $old = Get-BrowserConnection $before $browser
        $new = Get-BrowserConnection $after $browser
        Assert-Evidence ($old.connectionId -ne $new.connectionId) "Connection did not change after upgrade: $browser"
    }
    Assert-Processes 'processes-v2.json' $v2.InstallLocation
    Assert-Evidence ($upgrade.oldProcessesExited -and $upgrade.automaticReconnect) 'Automatic replacement was not observed.'
}

Test-Requirement 'UninstallLeavesExternalRegistration' {
    Assert-Evidence ($uninstall.remainingPackages -eq 0 -and $uninstall.remainingProcesses -eq 0 -and -not $uninstall.aliasExists) 'Package, alias, or processes survived uninstall.'
    Assert-Evidence ($uninstall.externalManifestExists) 'Expected the external manifest to survive uninstall.'
    Assert-Registration (Read-Evidence 'desktop-uninstalled.json') $external.manifest
    foreach ($browser in @('edge', 'chrome')) {
        $handshake = Get-Content -LiteralPath (Join-Path $evidence "handshake-uninstalled-$browser.txt") -Raw
        Assert-Evidence ($handshake -match '"error":"Specified native messaging host not found\."') "Unexpected post-uninstall response: $browser"
    }
}

$cleanup = Read-Evidence 'manual-cleanup.json'
Assert-Evidence ($cleanup.manualCleanup -and $cleanup.remainingKeys -eq 0 -and -not $cleanup.externalManifestExists -and $cleanup.packageIdentityStatus -eq 15700) 'Proof leftovers were not cleaned from the real desktop context.'
$final = Read-Evidence 'final-cleanup-check.json'
Assert-Evidence ($final.existingPackageUnchanged -and $final.existingManifestUnchanged -and $final.proofPackageAbsent -and $final.signingCertificateRetained) 'Incomplete cleanup or original installation changed.'
Assert-Evidence ($final.temporaryTrust.Count -eq 2 -and @($final.temporaryTrust | Where-Object present).Count -eq 0) 'Temporary certificate trust remains.'
[ordered]@{ observationsVerified = $true; checks = @($checks); automaticRegistryCleanup = $false; manualCleanupPassed = $true; evidenceDirectory = $evidence } | ConvertTo-Json -Depth 5
