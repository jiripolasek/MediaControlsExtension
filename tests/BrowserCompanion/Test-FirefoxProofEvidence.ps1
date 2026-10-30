[CmdletBinding()]
param([Parameter(Mandatory)][string] $EvidenceDirectory)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$checks = [Collections.Generic.List[object]]::new()
function Read-Result([string] $Name) { Get-Content -LiteralPath (Join-Path $EvidenceDirectory "$Name.json") -Raw | ConvertFrom-Json }
function Assert-True([bool] $Condition, [string] $Name) {
    if (!$Condition) { throw $Name }
    $checks.Add(@{ name = $Name; passed = $true })
}
function Read-Catalog([string] $Name) {
    $catalog = Read-Result "$Name-catalog"
    Assert-True ($catalog.connections.Count -eq 1 -and $catalog.connections[0].browser -eq 'Firefox' -and
        $catalog.connections[0].applicationId -match '^[A-Fa-f0-9]{16}$' -and $catalog.connections[0].hasSnapshot -and
        $catalog.connections[0].heartbeatAgeMs -ge 0 -and $catalog.connections[0].heartbeatAgeMs -lt 6000) "$Name has one complete Firefox catalog and a fresh heartbeat"
    $state = Read-Result "$Name-state"
    Assert-True ($state.processes.Count -eq 2 -and @($state.processes | Where-Object {
        !$_.Path.StartsWith($state.location + '\', [StringComparison]::OrdinalIgnoreCase)
    }).Count -eq 0) "$Name runs the bridge and relay from the installed package"
    return $catalog.connections[0]
}
function Has-Provider($Snapshot, [string] $Id) {
    return @($Snapshot.sessions | Where-Object { $_.source.Provider.Id -eq $Id }).Count -gt 0
}

$installed = Read-Result 'installed'
Assert-True ($installed.package -like '*_0.40.0.0_*' -and $installed.aliasExists -and $null -eq $installed.manifest -and
    $null -eq $installed.firefoxLauncherHash -and @($installed.registrations | Where-Object { $null -ne $_.value }).Count -eq 0) 'Install remains opt-in'
$registered = Read-Result 'register'
Assert-True ($registered.exitCode -eq 0 -and $registered.state.manifest.allowed_extensions.Count -eq 1 -and
    $registered.state.manifest.allowed_extensions[0] -eq 'media-controls-companion@jiripolasek.com' -and
    $registered.state.manifest.path.EndsWith('\firefox-bridge.cmd') -and $registered.state.firefoxLauncherHash -and
    @($registered.state.registrations | Where-Object { $_.value -like '*\firefox-native-host.json' }).Count -eq 2) 'Enable registers the exact Firefox ID and stable launcher in both registry views'
$v1 = Read-Catalog 'connected'
$v2 = Read-Catalog 'upgraded'
$before = Read-Result 'before-upgrade'
$after = Read-Result 'upgraded'
Assert-True ($after.package -like '*_0.40.0.1_*' -and $after.aliasExists -and $before.manifestHash -eq $after.manifestHash -and
    $before.firefoxLauncherHash -eq $after.firefoxLauncherHash -and ($before.registrations.value -join '|') -eq
    ($after.registrations.value -join '|')) 'Upgrade preserves registration and launcher without running setup'
Assert-True ($v1.connectionId -ne $v2.connectionId -and $v1.epoch -ne $v2.epoch -and $v1.profileId -eq $v2.profileId) 'Firefox reconnects after upgrade with a fresh connection and epoch'
foreach ($name in @('removed', 'again-removed')) {
    $removed = Read-Result $name
    Assert-True ($removed.exitCode -eq 0 -and $removed.state.aliasExists -and $removed.state.package -and
        $null -eq $removed.state.manifest -and $null -eq $removed.state.firefoxLauncherHash -and
        $removed.state.processes.Count -eq 0 -and @($removed.state.registrations | Where-Object { $null -ne $_.value }).Count -eq 0) "$name clears owned Firefox integration and stops bridge processes"
}
Assert-True (((Read-Result 'removed-errors') -join "`n") -match 'No such native application com.jiripolasek.media_controls') 'Firefox reports the missing native host after explicit removal'
$reenabled = Read-Catalog 'reenabled'
Assert-True ($reenabled.profileId -eq $v1.profileId -and $reenabled.connectionId -ne $v2.connectionId) 'Re-enable reconnects the same Firefox profile'
$media = Read-Result 'media-composite'
$applicationId = $reenabled.applicationId
Assert-True (@($media.raw | Where-Object applicationId -eq $applicationId).Count -eq 1 -and
    @($media.claims | Where-Object ApplicationId -eq $applicationId).Count -eq 2) 'Native Firefox identity matches its actual GSMTC session and both source claims'
Assert-True ((Has-Provider $media 'browser.companion') -and !(Has-Provider $media 'gsmtc')) 'Healthy companion suppresses the Firefox GSMTC duplicate'
foreach ($operation in @('pause', 'play', 'next')) {
    Assert-True ((Read-Result "$operation-command").status -eq 'completed') "Firefox completes the $operation media command"
}
$samples = @(Read-Result 'fallback-samples')
$fallback = Read-Result 'fallback'
Assert-True ($samples.Count -ge 45 -and $samples[0].claims.Count -eq 2 -and $fallback.claims.Count -eq 0 -and
    $fallback.elapsedMs -ge 54000 -and $fallback.elapsedMs -le 65000) 'Firefox fallback keeps the sixty-second heartbeat grace period'
$restored = Read-Result 'fallback-composite'
Assert-True ((Has-Provider $restored 'gsmtc') -and !(Has-Provider $restored 'browser.companion') -and
    @($restored.raw | Where-Object { $_.applicationId -eq $applicationId -and $_.playback -eq 'Playing' }).Count -eq 1) 'GSMTC playback returns when the companion stays unavailable'
$recovery = Read-Result 'recovery'
Assert-True ($recovery.elapsedMs -ge 10000 -and $recovery.elapsedMs -le 35000 -and
    (Has-Provider $recovery.snapshot 'browser.companion') -and !(Has-Provider $recovery.snapshot 'gsmtc')) 'Sustained Firefox recovery restores the companion without a GSMTC duplicate'
$beforeUninstall = Read-Result 'before-uninstall'
$uninstalled = Read-Result 'uninstalled'
Assert-True ($null -eq $uninstalled.package -and !$uninstalled.aliasExists -and $uninstalled.processes.Count -eq 0 -and
    $uninstalled.manifestHash -eq $beforeUninstall.manifestHash -and $uninstalled.firefoxLauncherHash -eq $beforeUninstall.firefoxLauncherHash -and
    ($uninstalled.registrations.value -join '|') -eq ($beforeUninstall.registrations.value -join '|')) 'Uninstall leaves the accepted inert registry entries, manifest and launcher'
Assert-True (((Read-Result 'uninstalled-errors') -join "`n") -match 'MediaControlsBrowserBridge.exe.*is not recognized') 'Firefox cannot launch the packaged bridge after uninstall'
$cleaned = Read-Result 'cleaned'
Assert-True ($null -eq $cleaned.package -and !$cleaned.aliasExists -and !$cleaned.trustedCertificatePresent -and
    $null -eq $cleaned.manifest -and $null -eq $cleaned.firefoxLauncherHash -and
    @($cleaned.registrations | Where-Object { $null -ne $_.value }).Count -eq 0) 'Proof cleanup removes owned integration and temporary guest certificate trust'
$null = Read-Result 'passed'
$result = @{ passed = $true; checks = @($checks); automaticUninstallRegistryCleanup = $false }
[IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'result.json'), ($result | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
Write-Host "$($checks.Count) Firefox lifecycle checks passed."
