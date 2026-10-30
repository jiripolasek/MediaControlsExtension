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
    $state = Read-Result "catalog-$Name"
    if ($state.exitCode -ne 0) { throw "Catalog failed: $Name" }
    $catalog = $state.catalog | ConvertFrom-Json
    Assert-True ($catalog.connections.Count -eq 2) "$Name contains exactly two browser connections"
    foreach ($id in @('MSEdgeDev', 'Chrome')) {
        $connections = @($catalog.connections | Where-Object applicationId -eq $id)
        Assert-True ($connections.Count -eq 1 -and $connections[0].hasSnapshot -and $connections[0].heartbeatAgeMs -ge 0 -and
            $connections[0].heartbeatAgeMs -lt 10000) "$Name has a complete healthy $id catalog"
    }
    Assert-True ($state.processes.Count -eq 3 -and @($state.processes | Where-Object { !$_.Path.StartsWith($state.package.InstallLocation + '\', [StringComparison]::OrdinalIgnoreCase) }).Count -eq 0) "$Name uses only installed package bridge processes"
    return $catalog
}

$installed = Read-Result 'installed'
Assert-True ($installed.package -like 'JiriPolasek.MediaControlsForCmdPal_0.40.0.0_*' -and $installed.aliasExists -and
    @($installed.registrations | Where-Object { $null -ne $_.value }).Count -eq 0) 'Install owns the launcher but registration remains opt-in'
$v1 = Read-Catalog 'v1-ready'
$v2 = Read-Catalog 'v2-ready'
$before = Read-Result 'before-upgrade'
$upgraded = Read-Result 'upgraded'
Assert-True ($upgraded.package -like '*_0.40.0.1_*' -and $upgraded.aliasExists -and $before.manifestHash -and
    $upgraded.manifestHash -eq $before.manifestHash -and
    ($before.registrations.value -join '|') -eq ($upgraded.registrations.value -join '|')) 'Upgrade preserves the external manifest and both registry views'
foreach ($id in @('MSEdgeDev', 'Chrome')) {
    $old = $v1.connections | Where-Object applicationId -eq $id
    $new = $v2.connections | Where-Object applicationId -eq $id
    Assert-True ($old.connectionId -ne $new.connectionId -and $old.epoch -ne $new.epoch -and $old.profileId -eq $new.profileId) "$id reconnects after upgrade without setup"
}
foreach ($name in @('removed', 'repeated-removed')) {
    $removed = Read-Result $name
    Assert-True ($removed.exitCode -eq 0 -and $removed.state.aliasExists -and $null -eq $removed.state.manifest -and
        $removed.state.processes.Count -eq 0 -and @($removed.state.registrations | Where-Object { $null -ne $_.value }).Count -eq 0) "$name clears owned integration and all live bridges"
}
$null = Read-Catalog 'readded-ready'
$beforeUninstall = Read-Result 'before-uninstall'
$uninstalled = Read-Result 'uninstalled'
Assert-True ($null -eq $uninstalled.package -and !$uninstalled.aliasExists -and $uninstalled.processes.Count -eq 0 -and
    $uninstalled.manifestHash -eq $beforeUninstall.manifestHash -and $null -ne $uninstalled.manifest -and
    ($uninstalled.registrations.value -join '|') -eq ($beforeUninstall.registrations.value -join '|')) 'Uninstall leaves the accepted inert manifest and registry entries'
foreach ($browser in @('Edge', 'Chrome')) {
    foreach ($phase in @('removed', 'uninstalled')) {
        $text = Get-Content -LiteralPath (Join-Path $EvidenceDirectory "$phase-$browser.txt") -Raw
        $result = [regex]::Match($text, '(?m)^\{[^\r\n]+\}').Value | ConvertFrom-Json
        Assert-True ($result.id -eq 'bfclnkfiooaolpmbhpnmhmjlibkeacge' -and $result.native.error -eq 'Specified native messaging host not found.') "$browser cannot connect after $phase"
    }
}
$cleaned = Read-Result 'cleaned'
Assert-True ($null -eq $cleaned.package -and !$cleaned.aliasExists -and !$cleaned.trustedCertificatePresent -and
    $null -eq $cleaned.manifest -and @($cleaned.registrations | Where-Object { $null -ne $_.value }).Count -eq 0) 'Proof cleanup removes only the test integration and temporary certificate trust'
$result = @{ passed = $true; checks = @($checks); automaticUninstallRegistryCleanup = $false }
[IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'result.json'), ($result | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
Write-Host "$($checks.Count) integrated package lifecycle checks passed."
